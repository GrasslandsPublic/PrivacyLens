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

        #endregion

        #region Initialization

        public GptChunkingService(IConfiguration configuration, ILogger<GptChunkingService> logger)
        {
            _logger = logger;

            // FIXED: Read from ChunkingAgent section first, then fall back to root
            var root = configuration.GetSection("AzureOpenAI");
            if (!root.Exists())
                throw new InvalidOperationException("Missing configuration section 'AzureOpenAI'.");

            var chunkingAgent = root.GetSection("ChunkingAgent");

            // Get endpoint from ChunkingAgent first, then fall back to root
            var endpoint = chunkingAgent["Endpoint"] ??
                root["Endpoint"] ??
                throw new InvalidOperationException("AzureOpenAI:ChunkingAgent:Endpoint not configured");

            var apiKey = chunkingAgent["ApiKey"] ??
                root["ApiKey"] ??
                throw new InvalidOperationException("AzureOpenAI:ChunkingAgent:ApiKey not configured");

            // Get deployment name from ChatDeployment in ChunkingAgent
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
        /// Main entry point for document chunking
        /// </summary>
        public async Task<IReadOnlyList<ChunkRecord>> ChunkAsync(
            string text,
            string? documentPath = null,
            Action<int, string>? onStream = null,
            CancellationToken ct = default)
        {
            try
            {
                LogDebug("CHUNK_START", $"Starting chunking for document: {documentPath ?? "unnamed"}");

                var tokenCount = CountTokens(text);
                LogDebug("TOKEN_COUNT", $"Document has {tokenCount} tokens");

                // Rate limiting
                await EnforceRateLimitAsync(tokenCount, ct);

                // Decide whether to use panelization
                if (tokenCount <= _singleWindowBudget || !_enablePanelization)
                {
                    LogDebug("SINGLE_WINDOW", $"Using single-window chunking (tokens: {tokenCount}, budget: {_singleWindowBudget})");
                    return await ChunkSingleWindowAsync(text, documentPath, onStream, ct);
                }
                else
                {
                    LogDebug("PANELIZATION", $"Using panelization (tokens: {tokenCount}, budget: {_singleWindowBudget})");
                    return await ChunkWithPanelizationAsync(text, documentPath, onStream, ct);
                }
            }
            catch (Exception ex)
            {
                LogError("CHUNK_ERROR", $"Error during chunking: {ex.Message}", ex);
                throw;
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
            var panels = CreatePanels(text);
            LogDebug("PANELS_CREATED", $"Created {panels.Count} panels");

            var allChunks = new List<ChunkRecord>();

            for (int i = 0; i < panels.Count; i++)
            {
                LogDebug("PANEL_PROCESS", $"Processing panel {i + 1}/{panels.Count}");

                var prompt = BuildChunkingPrompt(panels[i], isPanel: true, panelNumber: i + 1, totalPanels: panels.Count);
                var response = await CallGptAsync(prompt, onStream, ct);
                var chunks = ParseChunkingResponse(response, documentPath);

                // Adjust chunk indices for this panel
                foreach (var chunk in chunks)
                {
                    allChunks.Add(chunk with { Index = allChunks.Count });
                }
            }

            return allChunks;
        }

        private List<string> CreatePanels(string text)
        {
            var panels = new List<string>();
            var words = text.Split(' ');
            var currentPanel = new StringBuilder();
            var currentTokens = 0;

            foreach (var word in words)
            {
                var wordTokens = CountTokens(word);

                if (currentTokens + wordTokens > _targetPanelTokens && currentPanel.Length > 0)
                {
                    panels.Add(currentPanel.ToString());

                    // Start new panel with overlap
                    currentPanel.Clear();
                    currentTokens = 0;

                    // Add overlap from previous panel
                    var overlapStart = Math.Max(0, panels[panels.Count - 1].Length - (_overlapTokens * 4)); // Rough estimate
                    if (overlapStart < panels[panels.Count - 1].Length)
                    {
                        var overlap = panels[panels.Count - 1].Substring(overlapStart);
                        currentPanel.Append(overlap);
                        currentTokens = CountTokens(overlap);
                    }
                }

                if (currentPanel.Length > 0) currentPanel.Append(' ');
                currentPanel.Append(word);
                currentTokens += wordTokens;
            }

            if (currentPanel.Length > 0)
            {
                panels.Add(currentPanel.ToString());
            }

            return panels;
        }

        private string BuildChunkingPrompt(string text, bool isPanel, int panelNumber = 0, int totalPanels = 0)
        {
            var promptBuilder = new StringBuilder();

            promptBuilder.AppendLine("You are a document chunking specialist. Your task is to intelligently segment the following document into semantic chunks suitable for RAG (Retrieval-Augmented Generation).");
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("Requirements:");
            promptBuilder.AppendLine("1. Each chunk should be self-contained and meaningful");
            promptBuilder.AppendLine("2. Preserve important context within each chunk");
            promptBuilder.AppendLine("3. Target chunk size: 300-500 tokens");
            promptBuilder.AppendLine("4. Maintain document structure and flow");
            promptBuilder.AppendLine();

            if (isPanel)
            {
                promptBuilder.AppendLine($"Note: This is panel {panelNumber} of {totalPanels} from a larger document.");
                promptBuilder.AppendLine("Ensure chunks at panel boundaries maintain continuity.");
                promptBuilder.AppendLine();
            }

            promptBuilder.AppendLine("Format your response as a JSON array of chunks:");
            promptBuilder.AppendLine("[");
            promptBuilder.AppendLine("  {");
            promptBuilder.AppendLine("    \"content\": \"The actual text content of the chunk\",");
            promptBuilder.AppendLine("    \"metadata\": {");
            promptBuilder.AppendLine("      \"type\": \"paragraph|header|list|table|code|other\",");
            promptBuilder.AppendLine("      \"importance\": \"high|medium|low\"");
            promptBuilder.AppendLine("    }");
            promptBuilder.AppendLine("  }");
            promptBuilder.AppendLine("]");
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("Document to chunk:");
            promptBuilder.AppendLine("---BEGIN DOCUMENT---");
            promptBuilder.AppendLine(text);
            promptBuilder.AppendLine("---END DOCUMENT---");

            return promptBuilder.ToString();
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
                            onStream(response.Length, part.Text ?? "");
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
                // Extract JSON from response (GPT might include explanation text)
                var jsonStart = response.IndexOf('[');
                var jsonEnd = response.LastIndexOf(']') + 1;

                if (jsonStart < 0 || jsonEnd <= jsonStart)
                {
                    LogError("PARSE_ERROR", "Could not find JSON array in response");
                    // Fallback: treat entire response as single chunk
                    return new List<ChunkRecord>
                    {
                        new ChunkRecord(
                            Index: 0,
                            Content: response,
                            DocumentPath: documentPath ?? "unknown",
                            Embedding: null)
                    };
                }

                var json = response.Substring(jsonStart, jsonEnd - jsonStart);
                var chunks = System.Text.Json.JsonSerializer.Deserialize<List<dynamic>>(json);

                if (chunks == null || chunks.Count == 0)
                {
                    throw new InvalidOperationException("No chunks found in response");
                }

                var result = new List<ChunkRecord>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunkJson = chunks[i].ToString();
                    var chunkData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(chunkJson);

                    var content = chunkData.ContainsKey("content") ? chunkData["content"].ToString() : "";

                    result.Add(new ChunkRecord(
                        Index: i,
                        Content: content ?? "",
                        DocumentPath: documentPath ?? "unknown",
                        Embedding: null));
                }

                return result;
            }
            catch (Exception ex)
            {
                LogError("PARSE_ERROR", $"Error parsing chunking response: {ex.Message}", ex);
                // Fallback: treat entire response as single chunk
                return new List<ChunkRecord>
                {
                    new ChunkRecord(
                        Index: 0,
                        Content: response,
                        DocumentPath: documentPath ?? "unknown",
                        Embedding: null)
                };
            }
        }

        private int CountTokens(string text)
        {
            return _enc.CountTokens(text);
        }

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