// Services/GptChunkingService.cs - Enhanced with comprehensive debugging
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using PrivacyLens.Diagnostics;
using PrivacyLens.Models;
using SharpToken;

namespace PrivacyLens.Services
{
    public sealed class GptChunkingService : IChunkingService
    {
        private readonly ChatClient _chat;
        private readonly GptEncoding _enc = GptEncoding.GetEncodingForModel("gpt-4o");

        // Streaming settings
        private readonly bool _streamEnabled;
        private readonly int _streamPreviewChars;
        private readonly int _streamUpdateIntervalMs;

        // Chunking parameters
        private readonly bool _enablePanelization;
        private readonly int _targetPanelTokens;
        private readonly int _overlapTokens;
        private readonly int _maxOutputTokens;
        private readonly int _tpm;
        private readonly int _singleWindowBudget;
        private readonly bool _requireMaxCompletionTokens;

        // Debug settings
        private readonly string? _promptDumpDir;
        private readonly string? _streamDumpDir;
        private readonly bool _emitPanelInfo;

        // Enhanced debug mode
        private readonly bool _debugMode;
        private readonly bool _verboseDebug;
        private readonly string? _debugOutputDir;

        private static bool _loggedConfigOnce;

        public GptChunkingService(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            if (!aoai.Exists())
                throw new InvalidOperationException("Missing configuration section: AzureOpenAI");

            var agent = aoai.GetSection("ChunkingAgent");
            string endpointStr = agent["Endpoint"] ?? aoai["Endpoint"] ??
                throw new InvalidOperationException("AzureOpenAI:Endpoint is missing");
            string apiKey = agent["ApiKey"] ?? aoai["ApiKey"] ??
                throw new InvalidOperationException("AzureOpenAI:ApiKey is missing");
            string deployment = agent["ChatDeployment"] ?? aoai["ChatDeployment"] ??
                throw new InvalidOperationException("AzureOpenAI:ChatDeployment is missing");

            var azureClient = new AzureOpenAIClient(new Uri(endpointStr), new AzureKeyCredential(apiKey));
            _chat = azureClient.GetChatClient(deployment);

            var diag = aoai.GetSection("Diagnostics");
            _streamEnabled = diag.GetValue("StreamChunkingOutput", config.GetValue("Diagnostics:StreamChunkingOutput", true));
            _streamPreviewChars = diag.GetValue("StreamPreviewChars", config.GetValue("Diagnostics:StreamPreviewChars", 120));
            _streamUpdateIntervalMs = diag.GetValue("StreamUpdateIntervalMs", config.GetValue("Diagnostics:StreamUpdateIntervalMs", 250));
            _promptDumpDir = diag["PromptDumpDir"] ?? config["Diagnostics:PromptDumpDir"];
            _streamDumpDir = diag["StreamDumpDir"] ?? config["Diagnostics:StreamDumpDir"];
            _emitPanelInfo = diag.GetValue("EmitPanelInfo", config.GetValue("Diagnostics:EmitPanelInfo", false));

            // Enhanced debug settings
            _debugMode = diag.GetValue("DebugMode", true); // Default to true for debugging
            _verboseDebug = diag.GetValue("VerboseDebug", true);
            _debugOutputDir = diag["DebugOutputDir"] ?? Path.Combine(Path.GetTempPath(), "PrivacyLens_Debug");

            var chunk = aoai.GetSection("Chunking");
            _enablePanelization = chunk.GetValue("EnablePanelization", config.GetValue("Chunking:EnablePanelization", true));
            _targetPanelTokens = chunk.GetValue("TargetPanelTokens", config.GetValue("Chunking:TargetPanelTokens", 3000));
            _overlapTokens = chunk.GetValue("OverlapTokens", config.GetValue("Chunking:OverlapTokens", 400));
            _maxOutputTokens = chunk.GetValue("MaxOutputTokens", config.GetValue("Chunking:MaxOutputTokens", 4000)); // Increased for debugging
            _tpm = chunk.GetValue("Tpm", config.GetValue("Chunking:Tpm", 50_000));
            _singleWindowBudget = chunk.GetValue("SingleWindowBudget", config.GetValue("Chunking:SingleWindowBudget", 3500));
            _requireMaxCompletionTokens = agent.GetValue("RequireMaxCompletionTokens",
                aoai.GetValue("RequireMaxCompletionTokens", true));

            // Create debug directory if needed
            if (_debugMode && !string.IsNullOrWhiteSpace(_debugOutputDir))
            {
                Directory.CreateDirectory(_debugOutputDir);
            }
        }

        public async Task<IReadOnlyList<ChunkRecord>> ChunkAsync(
            string text,
            string? documentPath = null,
            Action<int, string>? onStream = null,
            CancellationToken ct = default)
        {
            if (!_loggedConfigOnce)
            {
                _loggedConfigOnce = true;
                var configMsg = $"@panel:config target={_targetPanelTokens} overlap={_overlapTokens} maxOut={_maxOutputTokens} tpm={_tpm} singleWin={_singleWindowBudget} requireMCT={_requireMaxCompletionTokens}";
                onStream?.Invoke(0, configMsg);
                LogDebug("CONFIGURATION", configMsg);
            }

            int totalTokens = CountTokens(text);

            LogDebug("INPUT_ANALYSIS", $"Input text tokens: {totalTokens}, Single window budget: {_singleWindowBudget}, Panelization enabled: {_enablePanelization}");

            // Save input text for debugging
            if (_verboseDebug)
            {
                await SaveDebugFile($"input_{DateTime.Now:yyyyMMdd_HHmmss}.txt", text);
            }

            if (!_enablePanelization || totalTokens <= _singleWindowBudget)
            {
                LogDebug("ROUTING", "Using single-window chunking");
                return await ChunkSingleWindowAsync(text, documentPath, onStream, ct);
            }

            LogDebug("ROUTING", "Using multi-panel chunking");
            return await ChunkMultiPanelAsync(text, documentPath, onStream, ct);
        }

        private async Task<IReadOnlyList<ChunkRecord>> ChunkSingleWindowAsync(
            string text,
            string? documentPath,
            Action<int, string>? onStream,
            CancellationToken ct)
        {
            LogDebug("SINGLE_WINDOW", "Entering single-window chunking mode");

            // Calculate token count for the prompt
            int totalTokens = CountTokens(text);
            int expectedChunks = Math.Max(2, (int)Math.Ceiling(totalTokens / 500.0));

            LogDebug("CHUNKING_PLAN", $"Document has {totalTokens} tokens, expecting approximately {expectedChunks} chunks");

            // Enhanced system prompt with multiple examples and clear instructions
            var systemPrompt = $@"You are a document chunking specialist. Your task is to split documents into semantically coherent chunks.

REQUIREMENTS:
1. Create chunks of 400-600 tokens each (approximately 2000-3000 characters)
2. The input document has approximately {totalTokens} tokens
3. You should create approximately {expectedChunks} chunks
4. Each chunk must be semantically complete (don't break mid-sentence)
5. Preserve ALL original text exactly as provided

CRITICAL INSTRUCTIONS:
1. Split the input into chunks of approximately 400-600 tokens each
2. Each chunk should be semantically complete (don't break mid-sentence)
3. Preserve all original text exactly as provided
4. Include ALL content from the document - don't skip anything
5. Output format is EXTREMELY IMPORTANT - follow it exactly

OUTPUT FORMAT RULES:
- Output the actual chunk content directly
- After each chunk (except the last), add a separator line containing EXACTLY these 11 characters: ---CHUNK---
- The separator must be on its own line
- Do not add any labels, numbers, or extra text
- Do not add markdown formatting or quotes around chunks

EXAMPLE OF CORRECT OUTPUT for a ~{totalTokens} token document (expecting ~{expectedChunks} chunks):
The Protection of Privacy Act (POPA) specifies the manner in which public bodies may collect personal information. This includes requirements for direct collection and providing notice to individuals about the purpose and authority for collection. [Continue for ~400-600 tokens]
---CHUNK---
Section 5 of POPA states that personal information must be collected directly from the individual it is about, unless there is an authority for indirect collection. The public body must inform the individual of the purpose, legal authority, and contact information. [Continue for ~400-600 tokens]
---CHUNK---
Collection notices must include the purpose statement, legal authority citation, and contact information for questions. This ensures transparency and allows individuals to make informed decisions about providing their personal information. [Continue with remaining content]

Remember: Output ONLY chunk text and separators. Include ALL content from the original document.";

            var userPrompt = $"Please chunk the following document according to the instructions:\n\n{text}";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            // Save prompts for debugging
            if (_debugMode)
            {
                await SaveDebugFile($"prompt_system_{DateTime.Now:yyyyMMdd_HHmmss}.txt", systemPrompt);
                await SaveDebugFile($"prompt_user_{DateTime.Now:yyyyMMdd_HHmmss}.txt", userPrompt);
            }

            var options = new ChatCompletionOptions();
            TrySetMaxTokens(options, _maxOutputTokens, onStream);

            LogDebug("API_CALL", $"Calling GPT with max_tokens={_maxOutputTokens}, streaming={_streamEnabled && onStream != null}");

            if (!_streamEnabled || onStream is null)
            {
                // Non-streaming path
                LogDebug("API_MODE", "Using non-streaming API call");

                var result = await _chat.CompleteChatAsync(messages, options, ct);
                var content = result.Value.Content?.FirstOrDefault()?.Text ?? string.Empty;

                LogDebug("RESPONSE", $"Received response length: {content.Length} chars");

                // Save raw response
                var responseFile = await SaveDebugFile($"response_raw_{DateTime.Now:yyyyMMdd_HHmmss}.txt", content);
                LogDebug("DEBUG_FILE", $"Raw response saved to: {responseFile}");

                // Analyze response for separators
                AnalyzeResponseForSeparators(content);

                return ParseChunks(content, documentPath);
            }
            else
            {
                // Streaming path with enhanced debugging
                LogDebug("API_MODE", "Using streaming API call");
                return await StreamAndParseChunksAsync(messages, options, documentPath, onStream, ct);
            }
        }

        private async Task<IReadOnlyList<ChunkRecord>> StreamAndParseChunksAsync(
            List<ChatMessage> messages,
            ChatCompletionOptions options,
            string? documentPath,
            Action<int, string> onStream,
            CancellationToken ct)
        {
            var stream = _chat.CompleteChatStreamingAsync(messages, options, ct);
            var sb = new StringBuilder(8192);
            var lastTick = Environment.TickCount;
            StreamWriter? dump = null;

            try
            {
                // Setup stream dump file
                if (!string.IsNullOrWhiteSpace(_debugOutputDir))
                {
                    var safe = TraceLog.MakeFileSafe(documentPath ?? $"doc-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                    var dumpPath = Path.Combine(_debugOutputDir, $"{safe}.stream_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    dump = new StreamWriter(new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                    await dump.WriteLineAsync($"# Stream for: {documentPath}");
                    await dump.WriteLineAsync($"# UTC Start: {DateTime.UtcNow:O}");
                    await dump.WriteLineAsync("# === STREAM CONTENT BELOW ===");
                    LogDebug("STREAM_DUMP", $"Stream dump file: {dumpPath}");
                }

                int chunkCount = 0;
                await foreach (var update in stream.WithCancellation(ct))
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                        {
                            sb.Append(part.Text);
                            if (dump != null)
                            {
                                await dump.WriteAsync(part.Text);
                                await dump.FlushAsync();
                            }

                            // Check for separator in stream
                            if (part.Text.Contains("---CHUNK---"))
                            {
                                chunkCount++;
                                LogDebug("STREAM_SEPARATOR", $"Found separator #{chunkCount} in stream");
                            }
                        }
                    }

                    var now = Environment.TickCount;
                    if (now - lastTick >= _streamUpdateIntervalMs)
                    {
                        lastTick = now;
                        var preview = $"[p 1/1] {Tail(sb, _streamPreviewChars)}";
                        onStream.Invoke(sb.Length, preview);
                    }
                }

                if (sb.Length > 0)
                {
                    onStream.Invoke(sb.Length, $"[p 1/1] {Tail(sb, _streamPreviewChars)}");
                }

                if (dump != null)
                {
                    await dump.WriteLineAsync("\n# === STREAM END ===");
                    await dump.WriteLineAsync($"# UTC End: {DateTime.UtcNow:O}");
                    await dump.WriteLineAsync($"# Total Length: {sb.Length} chars");
                    await dump.FlushAsync();
                }
            }
            finally
            {
                dump?.Dispose();
            }

            var finalContent = sb.ToString();

            LogDebug("STREAM_COMPLETE", $"Final streamed content length: {finalContent.Length} chars");

            // Save complete response
            var responseFile = await SaveDebugFile($"response_streamed_{DateTime.Now:yyyyMMdd_HHmmss}.txt", finalContent);
            LogDebug("DEBUG_FILE", $"Streamed response saved to: {responseFile}");

            // Analyze response for separators
            AnalyzeResponseForSeparators(finalContent);

            return ParseChunks(finalContent, documentPath);
        }

        private void AnalyzeResponseForSeparators(string content)
        {
            LogDebug("SEPARATOR_ANALYSIS", "Analyzing response for separators...");

            // Check for exact separator
            int exactCount = 0;
            int index = 0;
            var positions = new List<int>();

            while ((index = content.IndexOf("---CHUNK---", index)) != -1)
            {
                exactCount++;
                positions.Add(index);
                LogDebug("SEPARATOR_FOUND", $"Found '---CHUNK---' at position {index}");
                index += 11;
            }

            LogDebug("SEPARATOR_COUNT", $"Total exact separators found: {exactCount}");

            // Check for variations
            var variations = new[]
            {
                "--- CHUNK ---",
                "---chunk---",
                "CHUNK",
                "--CHUNK--",
                "===CHUNK===",
                "\n---\n",
                "---"
            };

            foreach (var variation in variations)
            {
                var count = content.Split(new[] { variation }, StringSplitOptions.None).Length - 1;
                if (count > 0)
                {
                    LogDebug("SEPARATOR_VARIANT", $"Found {count} instances of '{variation}'");
                }
            }

            // Show content snippets around expected separator locations
            if (_verboseDebug && content.Length > 100)
            {
                LogDebug("CONTENT_PREVIEW", "First 500 chars:");
                LogDebug("CONTENT", content.Substring(0, Math.Min(500, content.Length)));

                if (content.Length > 1000)
                {
                    LogDebug("CONTENT_PREVIEW", "Middle 500 chars:");
                    var midStart = content.Length / 2 - 250;
                    LogDebug("CONTENT", content.Substring(midStart, 500));
                }

                LogDebug("CONTENT_PREVIEW", "Last 500 chars:");
                LogDebug("CONTENT", content.Substring(Math.Max(0, content.Length - 500)));
            }
        }

        private static List<ChunkRecord> ParseChunks(string content, string? documentPath)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[DEBUG ParseChunks] Starting parsing...");
            Console.WriteLine($"[DEBUG ParseChunks] Input length: {content.Length} chars");
            Console.ResetColor();

            // Try multiple separator variations in order of preference
            var separatorAttempts = new[]
            {
                new { Separator = "\n---CHUNK---\n", Name = "\\n---CHUNK---\\n (standard with newlines)" },
                new { Separator = "---CHUNK---", Name = "---CHUNK--- (exact match)" },
                new { Separator = "\r\n---CHUNK---\r\n", Name = "\\r\\n---CHUNK---\\r\\n (Windows newlines)" },
                new { Separator = "\n---CHUNK---", Name = "\\n---CHUNK--- (leading newline)" },
                new { Separator = "---CHUNK---\n", Name = "---CHUNK---\\n (trailing newline)" },
                // Fallback patterns
                new { Separator = "\n---\n", Name = "\\n---\\n (simple dashes)" },
                new { Separator = "---", Name = "--- (just dashes)" }
            };

            string[] parts = null;
            string usedSeparator = null;

            foreach (var attempt in separatorAttempts)
            {
                if (content.Contains(attempt.Separator))
                {
                    parts = content.Split(new[] { attempt.Separator }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1) // Only use if it actually splits
                    {
                        usedSeparator = attempt.Name;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[DEBUG ParseChunks] Successfully split using: {usedSeparator}");
                        Console.WriteLine($"[DEBUG ParseChunks] Found {parts.Length} parts");
                        Console.ResetColor();
                        break;
                    }
                }
            }

            // If no separator found or only one part, try pattern-based splitting
            if (parts == null || parts.Length <= 1)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[DEBUG ParseChunks] No separator found, attempting pattern-based splitting...");
                Console.ResetColor();

                // Try to detect if there are numbered chunks or other patterns
                var lines = content.Split('\n');
                var potentialChunks = new List<string>();
                var currentChunk = new StringBuilder();

                foreach (var line in lines)
                {
                    // Check if line might be a chunk boundary (empty or contains only dashes/equals)
                    if (string.IsNullOrWhiteSpace(line) ||
                        line.Trim().All(c => c == '-' || c == '=' || c == '_'))
                    {
                        if (currentChunk.Length > 50) // Minimum chunk size
                        {
                            potentialChunks.Add(currentChunk.ToString().Trim());
                            currentChunk.Clear();
                        }
                    }
                    else
                    {
                        if (currentChunk.Length > 0) currentChunk.AppendLine();
                        currentChunk.Append(line);
                    }
                }

                // Add last chunk
                if (currentChunk.Length > 50)
                {
                    potentialChunks.Add(currentChunk.ToString().Trim());
                }

                if (potentialChunks.Count > 1)
                {
                    parts = potentialChunks.ToArray();
                    usedSeparator = "PATTERN_BASED (empty lines/dashes)";
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[DEBUG ParseChunks] Pattern-based split found {parts.Length} potential chunks");
                    Console.ResetColor();
                }
                else
                {
                    // Final fallback: treat entire content as one chunk
                    parts = new[] { content };
                    usedSeparator = "NONE (treating as single chunk)";
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[DEBUG ParseChunks] WARNING: No chunks detected, treating entire content as single chunk");
                    Console.ResetColor();
                }
            }

            // Log chunk details
            var chunks = new List<ChunkRecord>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                var chunkText = parts[i].Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    chunks.Add(new ChunkRecord(
                        DocumentPath: documentPath ?? "unknown",
                        Index: i,
                        Content: chunkText,
                        Embedding: Array.Empty<float>()
                    ));

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[DEBUG ParseChunks] Chunk {i + 1}: {chunkText.Length} chars, preview: {chunkText.Substring(0, Math.Min(100, chunkText.Length))}...");
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[DEBUG ParseChunks] Returning {chunks.Count} chunks");
            Console.ResetColor();

            return chunks;
        }

        private async Task<IReadOnlyList<ChunkRecord>> ChunkMultiPanelAsync(
            string text,
            string? documentPath,
            Action<int, string>? onStream,
            CancellationToken ct)
        {
            // Implementation for multi-panel chunking
            // (Keep existing implementation or add similar debugging)
            LogDebug("MULTI_PANEL", "Multi-panel chunking not yet implemented with enhanced debugging");

            // Fallback to single window for now
            return await ChunkSingleWindowAsync(text, documentPath, onStream, ct);
        }

        private void TrySetMaxTokens(ChatCompletionOptions options, int desired, Action<int, string>? onStream)
        {
            if (!_requireMaxCompletionTokens)
            {
                // Try setting without catching exception
                try
                {
                    options.MaxOutputTokenCount = desired;
                    LogDebug("MAX_TOKENS", $"Successfully set MaxOutputTokenCount to {desired}");
                }
                catch
                {
                    LogDebug("MAX_TOKENS", "Failed to set MaxOutputTokenCount, model may not support it");
                }
            }
            else
            {
                options.MaxOutputTokenCount = desired;
                LogDebug("MAX_TOKENS", $"Set MaxOutputTokenCount to {desired} (required mode)");
            }
        }

        private int CountTokens(string text) => _enc.CountTokens(text);

        private static string Tail(StringBuilder sb, int maxChars)
        {
            if (sb.Length <= maxChars) return sb.ToString();
            return "…" + sb.ToString(sb.Length - maxChars, maxChars);
        }

        private void LogDebug(string category, string message)
        {
            if (!_debugMode) return;

            var color = category switch
            {
                "ERROR" => ConsoleColor.Red,
                "WARNING" => ConsoleColor.Yellow,
                "SEPARATOR_FOUND" => ConsoleColor.Green,
                "SEPARATOR_COUNT" => ConsoleColor.Cyan,
                "API_CALL" => ConsoleColor.Magenta,
                _ => ConsoleColor.Gray
            };

            Console.ForegroundColor = color;
            Console.WriteLine($"[DEBUG {category}] {message}");
            Console.ResetColor();
        }

        private async Task<string> SaveDebugFile(string filename, string content)
        {
            if (!_debugMode || string.IsNullOrWhiteSpace(_debugOutputDir))
                return string.Empty;

            var fullPath = Path.Combine(_debugOutputDir, filename);
            await File.WriteAllTextAsync(fullPath, content);
            return fullPath;
        }

        // Simplified TokenPanel for building
        private record TokenPanel(string Text, int Tokens);

        private static IEnumerable<TokenPanel> BuildTokenPanels(string text, int targetTokens, int overlapTokens)
        {
            // Simple implementation for now
            yield return new TokenPanel(text, 0);
        }
    }
}