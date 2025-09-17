using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using OpenAI.Chat;
using PrivacyLens.Models;
using PrivacyLens.Diagnostics;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Service for chunking documents using GPT models with multi-panel support for large documents
    /// Enhanced with detailed reporting and statistics
    /// </summary>
    public sealed class GptChunkingService : IChunkingService, IAsyncDisposable
    {
        #region Fields and Properties

        private readonly ILogger<GptChunkingService> _logger;
        private readonly AzureOpenAIClient _client;
        private readonly ChatClient _chatClient;
        private readonly string _deploymentName;
        private readonly bool _enablePanelization;
        private readonly int _singleWindowBudget;
        private readonly int _targetPanelTokens;
        private readonly int _overlapTokens;
        private readonly int _maxOutputTokenCount;
        private readonly bool _useStreaming;
        private readonly bool _requireManifestContextTag;
        private readonly int _targetTokensPerMinute;
        private readonly bool _saveDebugFiles;
        private readonly string _debugPath;
        private readonly Tokenizer _enc;
        private readonly TraceLog? _trace;

        // Rate limiting
        private int _tokensUsedThisMinute = 0;
        private DateTime _currentMinuteStart = DateTime.UtcNow;
        private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);

        // Statistics tracking
        public class ChunkingStatistics
        {
            public int DocumentTokens { get; set; }
            public bool UsedPanelization { get; set; }
            public int PanelCount { get; set; }
            public int TotalChunks { get; set; }
            public int MinChunkTokens { get; set; } = int.MaxValue;
            public int MaxChunkTokens { get; set; }
            public long TotalChunkTokens { get; set; }
            public TimeSpan ProcessingTime { get; set; }

            public double AverageChunkTokens => TotalChunks > 0 ? (double)TotalChunkTokens / TotalChunks : 0;
        }

        private ChunkingStatistics? _lastStatistics;
        public ChunkingStatistics? LastStatistics => _lastStatistics;

        #endregion

        #region Initialization

        public GptChunkingService(IConfiguration configuration, ILogger<GptChunkingService> logger)
        {
            _logger = logger;

            var root = configuration.GetSection("AzureOpenAI");
            if (!root.Exists())
                throw new InvalidOperationException("Missing configuration section 'AzureOpenAI'.");

            var chunkingAgent = root.GetSection("ChunkingAgent");

            var endpoint = chunkingAgent["Endpoint"] ??
                root["Endpoint"] ??
                throw new InvalidOperationException("AzureOpenAI:ChunkingAgent:Endpoint not configured");

            var apiKey = chunkingAgent["ApiKey"] ??
                root["ApiKey"] ??
                throw new InvalidOperationException("AzureOpenAI:ChunkingAgent:ApiKey not configured");

            _deploymentName = chunkingAgent["ChatDeployment"] ??
                root["DeploymentName"] ??
                "gpt-4o-mini";

            // Load chunking configuration
            var chunkingConfig = root.GetSection("Chunking");
            _enablePanelization = chunkingConfig.GetValue<bool>("EnablePanelization", true);
            _singleWindowBudget = chunkingConfig.GetValue<int>("SingleWindowBudget", 3000);
            _targetPanelTokens = chunkingConfig.GetValue<int>("TargetPanelTokens", 2000);
            _overlapTokens = chunkingConfig.GetValue<int>("OverlapTokens", 400);
            _maxOutputTokenCount = chunkingConfig.GetValue<int>("MaxOutputTokenCount", 3000);
            _useStreaming = chunkingConfig.GetValue<bool>("UseStreaming", true);
            _requireManifestContextTag = chunkingConfig.GetValue<bool>("RequireManifestContextTag", false);
            _targetTokensPerMinute = chunkingConfig.GetValue<int>("TargetTokensPerMinute", 50000);
            _saveDebugFiles = chunkingConfig.GetValue<bool>("SaveDebugFiles", true);

            // Initialize tokenizer - using TikToken for GPT-4
            _enc = TiktokenTokenizer.CreateForModel("gpt-4");

            // Set up debug path
            _debugPath = Path.Combine(Path.GetTempPath(), "PrivacyLens_Debug");
            if (_saveDebugFiles)
            {
                Directory.CreateDirectory(_debugPath);
            }

            // Initialize Azure OpenAI client
            _client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            _chatClient = _client.GetChatClient(_deploymentName);

            // Initialize trace log if debug is enabled
            if (_saveDebugFiles)
            {
                _trace = new TraceLog(_debugPath, $"chunking_{DateTime.Now:yyyyMMdd_HHmmss}", true);
                _trace.InitAsync($"GptChunkingService initialized with deployment: {_deploymentName}").Wait();
            }

            LogDebug("INIT", $"Service initialized - Deployment: {_deploymentName}, Panelization: {_enablePanelization}, SingleWindow: {_singleWindowBudget}, PanelSize: {_targetPanelTokens}, Overlap: {_overlapTokens}");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Main entry point for document chunking with enhanced statistics
        /// </summary>
        public async Task<IReadOnlyList<ChunkRecord>> ChunkAsync(
            string text,
            string? documentPath = null,
            Action<int, string>? onStream = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                LogError("CHUNK_ERROR", "Empty or null text provided");
                return Array.Empty<ChunkRecord>();
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Initialize statistics
                _lastStatistics = new ChunkingStatistics
                {
                    DocumentTokens = CountTokens(text)
                };

                onStream?.Invoke(0, $"@stats:tokens Document contains {_lastStatistics.DocumentTokens} tokens");

                IReadOnlyList<ChunkRecord> chunks;

                // Decide chunking strategy based on document size
                if (_lastStatistics.DocumentTokens <= _singleWindowBudget || !_enablePanelization)
                {
                    onStream?.Invoke(0, "@stats:strategy Using single-window chunking");
                    _lastStatistics.UsedPanelization = false;
                    _lastStatistics.PanelCount = 1;

                    chunks = await ChunkSingleWindowAsync(text, documentPath, onStream, ct);
                }
                else
                {
                    onStream?.Invoke(0, "@stats:strategy Using multi-panel chunking");
                    _lastStatistics.UsedPanelization = true;

                    chunks = await ChunkWithPanelizationAsync(text, documentPath, onStream, ct);
                }

                // Update statistics
                _lastStatistics.TotalChunks = chunks.Count;

                foreach (var chunk in chunks)
                {
                    var tokenCount = CountTokens(chunk.Content);
                    _lastStatistics.TotalChunkTokens += tokenCount;
                    _lastStatistics.MinChunkTokens = Math.Min(_lastStatistics.MinChunkTokens, tokenCount);
                    _lastStatistics.MaxChunkTokens = Math.Max(_lastStatistics.MaxChunkTokens, tokenCount);
                }

                stopwatch.Stop();
                _lastStatistics.ProcessingTime = stopwatch.Elapsed;

                // Report final statistics
                var statsMessage = $"@stats:complete Generated {chunks.Count} chunks | " +
                    $"Tokens: min={_lastStatistics.MinChunkTokens}, avg={_lastStatistics.AverageChunkTokens:F0}, max={_lastStatistics.MaxChunkTokens} | " +
                    $"Time: {_lastStatistics.ProcessingTime.TotalSeconds:F1}s";

                onStream?.Invoke(0, statsMessage);
                LogDebug("CHUNK_COMPLETE", statsMessage);

                return chunks;
            }
            catch (Exception ex)
            {
                LogError("CHUNK_ERROR", $"Error during chunking: {ex.Message}", ex);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                if (_lastStatistics != null)
                    _lastStatistics.ProcessingTime = stopwatch.Elapsed;
            }
        }

        #endregion

        #region Private Methods

        private async Task<IReadOnlyList<ChunkRecord>> ChunkSingleWindowAsync(
            string text,
            string? documentPath,
            Action<int, string>? onStream,
            CancellationToken ct)
        {
            var prompt = BuildChunkingPrompt(text, isPanel: false);

            // Report that we're processing
            onStream?.Invoke(0, "@chunk:processing Sending to GPT for chunking...");

            var response = await CallGptAsync(prompt, null, ct); // Don't pass onStream to avoid token spam

            onStream?.Invoke(0, "@chunk:parsing Parsing response into chunks...");

            return ParseChunkingResponse(response, documentPath);
        }

        private async Task<IReadOnlyList<ChunkRecord>> ChunkWithPanelizationAsync(
            string text,
            string? documentPath,
            Action<int, string>? onStream,
            CancellationToken ct)
        {
            LogDebug("PANELIZATION_START", "Starting document panelization");

            // Build panels
            var panels = BuildTokenPanels(text, _targetPanelTokens, _overlapTokens);
            LogDebug("PANELS_CREATED", $"Created {panels.Count} panels");
            _lastStatistics!.PanelCount = panels.Count;

            onStream?.Invoke(0, $"@panel:info Processing {panels.Count} panels with {_overlapTokens} token overlap");

            var allChunks = new List<ChunkRecord>();

            for (int i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                onStream?.Invoke(0, $"@panel:progress Processing panel {i + 1}/{panels.Count}");

                LogDebug("PANEL_PROCESS", $"Processing panel {i + 1}/{panels.Count} (tokens {panel.StartTokenIndex}-{panel.EndTokenIndex})");

                // Build panel-specific prompt
                var panelPrompt = BuildPanelPrompt(panel.Text, i + 1, panels.Count, i > 0);

                // Get chunks for this panel - DON'T pass onStream to avoid token spam
                var panelResponse = await CallGptAsync(panelPrompt, null, ct);
                var panelChunks = ParseChunkingResponse(panelResponse, documentPath);

                LogDebug("PANEL_CHUNKS", $"Panel {i + 1} generated {panelChunks.Count} chunks");

                // Stitch chunks (remove last chunk from previous panel if not first panel)
                if (i > 0 && allChunks.Count > 0)
                {
                    // Remove the last chunk from previous panel (likely incomplete)
                    allChunks.RemoveAt(allChunks.Count - 1);
                }

                // Add all chunks from current panel
                allChunks.AddRange(panelChunks);

                onStream?.Invoke(0, $"@panel:chunks Panel {i + 1} complete: {panelChunks.Count} chunks added");
            }

            // Re-index all chunks
            for (int i = 0; i < allChunks.Count; i++)
            {
                allChunks[i] = allChunks[i] with { Index = i };
            }

            onStream?.Invoke(0, $"@panel:complete Assembled {allChunks.Count} chunks from {panels.Count} panels");

            return allChunks;
        }

        private string BuildChunkingPrompt(string text, bool isPanel)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine("You are a document chunking specialist. Your task is to split the following document into semantically coherent chunks suitable for a RAG system.");
            prompt.AppendLine();
            prompt.AppendLine("REQUIREMENTS:");
            prompt.AppendLine("1. Each chunk should be approximately 300-500 tokens (roughly 200-400 words)");
            prompt.AppendLine("2. Maintain semantic boundaries - don't split sentences or paragraphs mid-way");
            prompt.AppendLine("3. Preserve the original text exactly, including all formatting, punctuation, and whitespace");
            prompt.AppendLine("4. Each chunk should be self-contained enough to be understood independently");
            prompt.AppendLine("5. Keep related information together (e.g., a heading with its content)");
            prompt.AppendLine();
            prompt.AppendLine("OUTPUT FORMAT:");
            prompt.AppendLine("- Output ONLY the chunked text with separators");
            prompt.AppendLine("- Do NOT add any explanations, numbering, or commentary");
            prompt.AppendLine("- Separate each chunk with EXACTLY this line (on its own line): ---CHUNK---");
            prompt.AppendLine("- Do NOT include ---CHUNK--- before the first chunk or after the last chunk");
            prompt.AppendLine();
            prompt.AppendLine("EXAMPLE OUTPUT FORMAT:");
            prompt.AppendLine("First chunk content here...");
            prompt.AppendLine("---CHUNK---");
            prompt.AppendLine("Second chunk content here...");
            prompt.AppendLine("---CHUNK---");
            prompt.AppendLine("Third chunk content here...");
            prompt.AppendLine();
            prompt.AppendLine("Document to chunk:");
            prompt.AppendLine("================================================================================");
            prompt.AppendLine(text);
            prompt.AppendLine("================================================================================");

            return prompt.ToString();
        }

        private string BuildPanelPrompt(string panelText, int panelNumber, int totalPanels, bool hasOverlap)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($"You are processing panel {panelNumber} of {totalPanels} from a larger document.");
            prompt.AppendLine();
            prompt.AppendLine("REQUIREMENTS:");
            prompt.AppendLine("1. Create chunks of approximately 300-500 tokens each");
            prompt.AppendLine("2. Maintain semantic boundaries - don't split sentences mid-way");
            prompt.AppendLine("3. Preserve the original text exactly, including all formatting");

            if (hasOverlap)
            {
                prompt.AppendLine($"4. IMPORTANT: The first {_overlapTokens} tokens are overlap from the previous panel for context.");
                prompt.AppendLine("   Start your FIRST chunk immediately AFTER this overlap section.");
                prompt.AppendLine("   Do not include the overlap text in any of your chunks.");
            }

            prompt.AppendLine();
            prompt.AppendLine("OUTPUT FORMAT:");
            prompt.AppendLine("- Output ONLY the chunked text with separators");
            prompt.AppendLine("- Separate chunks with EXACTLY this line: ---CHUNK---");
            prompt.AppendLine("- No explanations, numbering, or commentary");
            prompt.AppendLine();
            prompt.AppendLine("Panel text:");
            prompt.AppendLine("================================================================================");
            prompt.AppendLine(panelText);
            prompt.AppendLine("================================================================================");

            return prompt.ToString();
        }

        private async Task<string> CallGptAsync(string prompt, Action<int, string>? onStream, CancellationToken ct)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage("You are a document processing assistant specialized in intelligent text chunking for RAG systems. Follow the instructions precisely and output only what is requested."),
                    new UserChatMessage(prompt)
                };

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = _maxOutputTokenCount,
                    Temperature = 0.3f  // Lower temperature for more consistent output
                };

                if (_useStreaming && onStream != null)
                {
                    // If we want streaming, collect the response but DON'T report each token
                    var response = new StringBuilder();

                    await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, options, ct))
                    {
                        foreach (var part in update.ContentUpdate)
                        {
                            if (part.Text != null)
                            {
                                response.Append(part.Text);
                            }
                        }
                    }

                    return response.ToString();
                }
                else
                {
                    // Non-streaming path
                    var result = await _chatClient.CompleteChatAsync(messages, options, ct);
                    return result.Value.Content[0].Text;
                }
            }
            catch (Exception ex)
            {
                LogError("GPT_CALL_ERROR", $"Error calling GPT: {ex.Message}", ex);
                throw;
            }
        }

        private List<ChunkRecord> ParseChunkingResponse(string response, string? documentPath)
        {
            try
            {
                // Clean up the response
                response = response?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(response))
                {
                    LogError("PARSE_ERROR", "Empty response from GPT");
                    return new List<ChunkRecord>();
                }

                // Log the first 500 chars of response for debugging
                LogDebug("PARSE_START", $"Response preview: {response.Substring(0, Math.Min(500, response.Length))}...");

                var chunks = new List<ChunkRecord>();
                var separator = "---CHUNK---";

                // Split by the separator
                var parts = response.Split(new[] { separator }, StringSplitOptions.None);

                LogDebug("PARSE_SPLIT", $"Response split into {parts.Length} parts");

                // Process each part
                for (int i = 0; i < parts.Length; i++)
                {
                    var content = parts[i].Trim();

                    // Skip empty chunks
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        LogDebug("PARSE_SKIP", $"Skipping empty part {i + 1}");
                        continue;
                    }

                    // Skip very short chunks (less than 50 chars is probably an error)
                    if (content.Length < 50)
                    {
                        LogDebug("PARSE_WARNING", $"Skipping very short chunk ({content.Length} chars): {content.Substring(0, Math.Min(content.Length, 50))}");
                        continue;
                    }

                    chunks.Add(new ChunkRecord(
                        Index: chunks.Count,
                        Content: content,
                        DocumentPath: documentPath ?? "unknown",
                        Embedding: Array.Empty<float>()
                    ));

                    LogDebug("PARSE_CHUNK", $"Added chunk {chunks.Count}: {content.Length} chars, ~{CountTokens(content)} tokens");
                }

                if (chunks.Count == 0)
                {
                    LogError("PARSE_ERROR", "No valid chunks found in response");

                    // Fallback: if the response looks substantial, treat it as a single chunk
                    if (!string.IsNullOrWhiteSpace(response) && response.Length > 100)
                    {
                        LogDebug("PARSE_FALLBACK", "Using entire response as single chunk");
                        chunks.Add(new ChunkRecord(
                            Index: 0,
                            Content: response,
                            DocumentPath: documentPath ?? "unknown",
                            Embedding: Array.Empty<float>()
                        ));
                    }
                }
                else
                {
                    LogDebug("PARSE_SUCCESS", $"Successfully parsed {chunks.Count} chunks");
                }

                return chunks;
            }
            catch (Exception ex)
            {
                LogError("PARSE_ERROR", $"Error parsing response: {ex.Message}", ex);

                // Last resort fallback
                if (!string.IsNullOrWhiteSpace(response))
                {
                    return new List<ChunkRecord>
                    {
                        new ChunkRecord(
                            Index: 0,
                            Content: response,
                            DocumentPath: documentPath ?? "unknown",
                            Embedding: Array.Empty<float>()
                        )
                    };
                }

                return new List<ChunkRecord>();
            }
        }

        private List<TokenPanel> BuildTokenPanels(string text, int targetPanelSize, int overlapSize)
        {
            var panels = new List<TokenPanel>();
            var tokens = _enc.EncodeToIds(text);

            if (tokens.Count <= targetPanelSize)
            {
                // Document fits in a single panel
                panels.Add(new TokenPanel(text, 0, tokens.Count - 1));
                return panels;
            }

            int position = 0;
            while (position < tokens.Count)
            {
                int startPos = position;
                int endPos = Math.Min(position + targetPanelSize, tokens.Count);

                // Extract text for this panel
                var panelTokens = tokens.Skip(startPos).Take(endPos - startPos).ToArray();
                var panelText = _enc.Decode(panelTokens);

                panels.Add(new TokenPanel(panelText, startPos, endPos - 1));

                // Move position forward, accounting for overlap
                position = endPos - overlapSize;

                // Prevent infinite loop if overlap is too large
                if (position <= startPos)
                {
                    position = endPos;
                }

                // If we're close to the end, just include everything in the last panel
                if (tokens.Count - position < overlapSize)
                {
                    break;
                }
            }

            LogDebug("PANELS", $"Created {panels.Count} panels from {tokens.Count} tokens");
            return panels;
        }

        private record TokenPanel(string Text, int StartTokenIndex, int EndTokenIndex);

        private int CountTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            try
            {
                return _enc.EncodeToIds(text).Count;
            }
            catch (Exception ex)
            {
                LogError("TOKEN_COUNT_ERROR", $"Error counting tokens: {ex.Message}", ex);
                // Fallback to rough estimate
                return text.Length / 4;
            }
        }

        #endregion

        #region Rate Limiting

        private async Task EnforceRateLimitAsync(int tokenCount, CancellationToken ct)
        {
            await _rateLimitSemaphore.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _currentMinuteStart).TotalMinutes >= 1)
                {
                    _currentMinuteStart = now;
                    _tokensUsedThisMinute = 0;
                }

                if (_tokensUsedThisMinute + tokenCount > _targetTokensPerMinute)
                {
                    var waitTime = 60 - (now - _currentMinuteStart).TotalSeconds;
                    if (waitTime > 0)
                    {
                        LogDebug("RATE_LIMIT", $"Rate limit reached. Waiting {waitTime:F1} seconds...");
                        await Task.Delay(TimeSpan.FromSeconds(waitTime), ct);
                        _currentMinuteStart = DateTime.UtcNow;
                        _tokensUsedThisMinute = 0;
                    }
                }

                _tokensUsedThisMinute += tokenCount;
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }

        #endregion

        #region Logging

        private void LogDebug(string category, string message)
        {
            _logger.LogDebug($"[{category}] {message}");
            _trace?.WriteLineAsync($"[{category}] {message}").Wait();
        }

        private void LogError(string category, string message, Exception? ex = null)
        {
            _logger.LogError(ex, $"[{category}] {message}");
            _trace?.WriteLineAsync($"[{category}] ERROR: {message} - {ex?.Message}").Wait();
        }

        #endregion

        #region IDisposable

        public async ValueTask DisposeAsync()
        {
            if (_trace != null)
            {
                await _trace.DisposeAsync();
            }
            _rateLimitSemaphore?.Dispose();
        }

        #endregion
    }
}