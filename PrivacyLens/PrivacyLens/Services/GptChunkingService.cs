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
            var stopwatch = Stopwatch.StartNew();
            _lastStatistics = new ChunkingStatistics();

            try
            {
                LogDebug("CHUNK_START", $"Starting chunking for document: {documentPath ?? "unnamed"}");

                var tokenCount = CountTokens(text);
                _lastStatistics.DocumentTokens = tokenCount;

                LogDebug("TOKEN_COUNT", $"Document has {tokenCount} tokens");

                // Report initial statistics
                onStream?.Invoke(0, $"@stats:tokens Document contains {tokenCount} tokens");

                // Rate limiting
                await EnforceRateLimitAsync(tokenCount, ct);

                IReadOnlyList<ChunkRecord> chunks;

                // Decide whether to use panelization
                if (tokenCount <= _singleWindowBudget || !_enablePanelization)
                {
                    LogDebug("SINGLE_WINDOW", $"Using single-window chunking (tokens: {tokenCount}, budget: {_singleWindowBudget})");
                    onStream?.Invoke(0, "@stats:strategy Using single-window chunking");

                    _lastStatistics.UsedPanelization = false;
                    _lastStatistics.PanelCount = 1;

                    chunks = await ChunkSingleWindowAsync(text, documentPath, onStream, ct);
                }
                else
                {
                    LogDebug("PANELIZATION", $"Using panelization (tokens: {tokenCount}, budget: {_singleWindowBudget})");

                    // Calculate panel count
                    int stride = _targetPanelTokens - _overlapTokens;
                    int estimatedPanels = (int)Math.Ceiling((double)(tokenCount - _overlapTokens) / stride);

                    onStream?.Invoke(0, $"@stats:strategy Using panelization with ~{estimatedPanels} panels");

                    _lastStatistics.UsedPanelization = true;
                    _lastStatistics.PanelCount = estimatedPanels;

                    chunks = await ChunkWithPanelizationAsync(text, documentPath, onStream, ct);
                }

                // Calculate chunk statistics
                _lastStatistics.TotalChunks = chunks.Count;
                foreach (var chunk in chunks)
                {
                    var chunkTokens = CountTokens(chunk.Content);
                    _lastStatistics.TotalChunkTokens += chunkTokens;
                    _lastStatistics.MinChunkTokens = Math.Min(_lastStatistics.MinChunkTokens, chunkTokens);
                    _lastStatistics.MaxChunkTokens = Math.Max(_lastStatistics.MaxChunkTokens, chunkTokens);
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
            var response = await CallGptAsync(prompt, onStream, ct);
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

            onStream?.Invoke(0, $"@panel:info Processing {panels.Count} panels with {_overlapTokens} token overlap");

            var allChunks = new List<ChunkRecord>();

            for (int i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                onStream?.Invoke(0, $"@panel:progress Processing panel {i + 1}/{panels.Count}");

                LogDebug("PANEL_PROCESS", $"Processing panel {i + 1}/{panels.Count} (tokens {panel.StartTokenIndex}-{panel.EndTokenIndex})");

                // Build panel-specific prompt
                var panelPrompt = BuildPanelPrompt(panel.Text, i + 1, panels.Count, i > 0);

                // Get chunks for this panel
                var panelResponse = await CallGptAsync(panelPrompt, onStream, ct);
                var panelChunks = ParseChunkingResponse(panelResponse, documentPath);

                LogDebug("PANEL_CHUNKS", $"Panel {i + 1} generated {panelChunks.Count} chunks");

                // Stitch chunks (handle overlaps)
                if (i == 0)
                {
                    allChunks.AddRange(panelChunks);
                }
                else
                {
                    // Remove last chunk from previous panel (likely incomplete)
                    // and add all chunks from current panel
                    if (allChunks.Count > 0)
                    {
                        LogDebug("STITCHING", $"Removing last chunk from previous panel to avoid overlap");
                        allChunks.RemoveAt(allChunks.Count - 1);
                    }
                    allChunks.AddRange(panelChunks);
                }

                onStream?.Invoke(0, $"@panel:chunks Panel {i + 1} complete: {panelChunks.Count} chunks added");
            }

            // Renumber chunks to ensure continuous indexing
            for (int i = 0; i < allChunks.Count; i++)
            {
                allChunks[i] = allChunks[i] with { Index = i };
            }

            LogDebug("PANELIZATION_COMPLETE", $"Panelization complete: {allChunks.Count} total chunks");
            onStream?.Invoke(0, $"@panel:complete Panelization complete: {allChunks.Count} total chunks");

            if (_lastStatistics != null)
                _lastStatistics.PanelCount = panels.Count;

            return allChunks;
        }

        private List<TokenPanel> BuildTokenPanels(string text, int targetTokens, int overlapTokens)
        {
            var allTokens = _enc.EncodeToIds(text);
            var panels = new List<TokenPanel>();

            int stride = targetTokens - overlapTokens;
            if (stride <= 0) stride = 1;

            for (int start = 0; start < allTokens.Count; start += stride)
            {
                int end = Math.Min(start + targetTokens, allTokens.Count);
                var panelTokens = allTokens.Skip(start).Take(end - start).ToArray();
                var panelText = _enc.Decode(panelTokens);

                panels.Add(new TokenPanel(panelText, start, end));

                if (end >= allTokens.Count) break;
            }

            return panels;
        }

        private record TokenPanel(string Text, int StartTokenIndex, int EndTokenIndex);

        private string BuildChunkingPrompt(string text, bool isPanel)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine("Please chunk this document into semantic sections suitable for RAG.");
            prompt.AppendLine("Requirements:");
            prompt.AppendLine("- Each chunk should be 400-600 tokens");
            prompt.AppendLine("- Preserve semantic boundaries (don't split sentences)");
            prompt.AppendLine("- Maintain original formatting including markdown");
            prompt.AppendLine();
            prompt.AppendLine("Return ONLY the chunks separated by '---CHUNK---' markers.");
            prompt.AppendLine();
            prompt.AppendLine("Document:");
            prompt.AppendLine(text);

            return prompt.ToString();
        }

        private string BuildPanelPrompt(string text, int panelNumber, int totalPanels, bool hasOverlap)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine($"You are processing panel {panelNumber} of {totalPanels} from a larger document.");

            if (hasOverlap)
            {
                prompt.AppendLine($"IMPORTANT: The first ~{_overlapTokens} tokens are overlap from the previous panel for context.");
                prompt.AppendLine("Start your first NEW chunk AFTER this overlap region.");
            }

            prompt.AppendLine();
            prompt.AppendLine("Chunk this panel into semantic sections (400-600 tokens each).");
            prompt.AppendLine("Return ONLY the chunks separated by '---CHUNK---' markers.");
            prompt.AppendLine();
            prompt.AppendLine("Panel text:");
            prompt.AppendLine(text);

            return prompt.ToString();
        }

        private async Task<string> CallGptAsync(string prompt, Action<int, string>? onStream, CancellationToken ct)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage("You are a document processing assistant specialized in intelligent text chunking for RAG systems."),
                    new UserChatMessage(prompt)
                };

                if (_useStreaming && onStream != null)
                {
                    var response = new StringBuilder();
                    var options = new ChatCompletionOptions
                    {
                        MaxOutputTokenCount = _maxOutputTokenCount
                    };

                    await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, options, ct))
                    {
                        foreach (var part in update.ContentUpdate)
                        {
                            response.Append(part.Text);
                            // Filter out stats messages from streaming to user
                            if (part.Text != null && !part.Text.StartsWith("@"))
                            {
                                onStream(response.Length, part.Text);
                            }
                        }
                    }

                    return response.ToString();
                }
                else
                {
                    var options = new ChatCompletionOptions
                    {
                        MaxOutputTokenCount = _maxOutputTokenCount
                    };

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
                // Try to parse as chunks separated by markers
                var chunks = new List<ChunkRecord>();
                var separator = "---CHUNK---";
                var parts = response.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < parts.Length; i++)
                {
                    var content = parts[i].Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        chunks.Add(new ChunkRecord(
                            Index: i,
                            Content: content,
                            DocumentPath: documentPath ?? "unknown",
                            Embedding: Array.Empty<float>()
                        ));
                    }
                }

                if (chunks.Count == 0)
                {
                    LogError("PARSE_ERROR", "No chunks found in response, treating entire response as single chunk");
                    chunks.Add(new ChunkRecord(
                        Index: 0,
                        Content: response,
                        DocumentPath: documentPath ?? "unknown",
                        Embedding: Array.Empty<float>()
                    ));
                }

                return chunks;
            }
            catch (Exception ex)
            {
                LogError("PARSE_ERROR", $"Error parsing response: {ex.Message}", ex);
                // Fallback: treat entire response as single chunk
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
        }

        private int CountTokens(string text)
        {
            return _enc.EncodeToIds(text).Count;
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