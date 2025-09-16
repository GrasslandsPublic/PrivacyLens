// Services/GptChunkingService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using PrivacyLens.Diagnostics;
using PrivacyLens.Models;
using SharpToken; // tokenizer (tiktoken port)

namespace PrivacyLens.Services
{
    /// <summary>
    /// GPT-driven chunker with TOKEN-BASED panelization:
    /// - Large inputs are split into token windows (TargetPanelTokens) with OverlapTokens continuity.
    /// - Each panel is sent as its own Chat call (streamed), then results are stitched (dedupe overlap).
    /// - A pacing rule spaces calls to stay under configured Tokens-per-Minute (TPM).
    /// - Emits panel lifecycle markers via onStream: "@panel:config", "@panel:start/accepted/done".
    /// - Handles GPT-5 models that require 'max_completion_tokens' (avoids sending legacy 'max_tokens').
    /// 
    /// This version reads model settings from:
    ///   AzureOpenAI:ChunkingAgent:*  (Endpoint/ApiKey/ApiVersion/ChatDeployment/RequireMaxCompletionTokens)
    /// and shared settings from:
    ///   AzureOpenAI:*                 (EmbeddingDeployment, Retry, Diagnostics, Chunking (panelization knobs))
    /// with fallbacks to legacy root sections when present.
    /// </summary>
    public sealed class GptChunkingService : IChunkingService
    {
        private readonly ChatClient _chat;

        // Streaming / diagnostics knobs
        private readonly bool _streamEnabled;
        private readonly int _streamPreviewChars;
        private readonly int _streamUpdateIntervalMs;
        private readonly string? _promptDumpDir;
        private readonly string? _streamDumpDir;
        private readonly bool _emitPanelInfo; // reduce console noise by default

        // Panelization knobs (from AzureOpenAI:Chunking)
        private readonly bool _enablePanelization;
        private readonly int _targetPanelTokens;
        private readonly int _overlapTokens;
        private readonly int _maxOutputTokens;
        private readonly int _tpm;
        private readonly int _singleWindowBudget;

        // Token contract knobs
        private readonly bool _requireMaxCompletionTokens; // GPT-5 models want MaxCompletionTokens (no legacy)
        private bool _loggedConfigOnce;

        // Tokenizer (o200k_base via gpt-4o); adjust if you deploy a different chat model
        private static readonly GptEncoding Enc = GptEncoding.GetEncodingForModel("gpt-4o");

        public GptChunkingService(IConfiguration config)
        {
            // --- Resolve Azure OpenAI sections ---
            var aoai = config.GetSection("AzureOpenAI");
            if (!aoai.Exists())
                throw new InvalidOperationException("Missing configuration section 'AzureOpenAI'.");

            // Prefer the specialized ChunkingAgent (gpt-4.1-mini in your case); fall back to shared defaults
            var agent = aoai.GetSection("ChunkingAgent");
            string endpointStr =
                agent["Endpoint"]
                ?? aoai["Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is missing");

            string apiKey =
                agent["ApiKey"]
                ?? aoai["ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is missing");

            string deployment =
                agent["ChatDeployment"]
                ?? aoai["ChatDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAI:ChatDeployment is missing");

            // Optional API version (not strictly required by the .NET v2 client)
            string? apiVersion =
                agent["ApiVersion"] ?? aoai["ApiVersion"]; // reserved if you later need custom options

            // Build client bound to the agent endpoint
            var azureClient = new AzureOpenAIClient(new Uri(endpointStr), new AzureKeyCredential(apiKey));
            _chat = azureClient.GetChatClient(deployment);

            // --- Diagnostics (prefer AzureOpenAI:Diagnostics; fallback to legacy root) ---
            var diag = aoai.GetSection("Diagnostics");
            _streamEnabled = diag.GetValue("StreamChunkingOutput", config.GetValue("Diagnostics:StreamChunkingOutput", true));
            _streamPreviewChars = diag.GetValue("StreamPreviewChars", config.GetValue("Diagnostics:StreamPreviewChars", 120));
            _streamUpdateIntervalMs = diag.GetValue("StreamUpdateIntervalMs", config.GetValue("Diagnostics:StreamUpdateIntervalMs", 250));
            _promptDumpDir = diag["PromptDumpDir"] ?? config["Diagnostics:PromptDumpDir"];
            _streamDumpDir = diag["StreamDumpDir"] ?? config["Diagnostics:StreamDumpDir"];
            _emitPanelInfo = diag.GetValue("EmitPanelInfo", config.GetValue("Diagnostics:EmitPanelInfo", false));

            // --- Panelization knobs (prefer AzureOpenAI:Chunking; fallback to legacy root "Chunking") ---
            var chunk = aoai.GetSection("Chunking");
            _enablePanelization = chunk.GetValue("EnablePanelization", config.GetValue("Chunking:EnablePanelization", true));
            _targetPanelTokens = chunk.GetValue("TargetPanelTokens", config.GetValue("Chunking:TargetPanelTokens", 3000));
            _overlapTokens = chunk.GetValue("OverlapTokens", config.GetValue("Chunking:OverlapTokens", 400));
            _maxOutputTokens = chunk.GetValue("MaxOutputTokens", config.GetValue("Chunking:MaxOutputTokens", 700));
            _tpm = chunk.GetValue("Tpm", config.GetValue("Chunking:Tpm", 50_000));
            _singleWindowBudget = chunk.GetValue("SingleWindowBudget", config.GetValue("Chunking:SingleWindowBudget", 3500));

            // --- Token contract behavior (prefer agent override; fallback to shared AzureOpenAI default; legacy default = true) ---
            _requireMaxCompletionTokens =
                agent.GetValue("RequireMaxCompletionTokens",
                    aoai.GetValue("RequireMaxCompletionTokens",
                        true));
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
                onStream?.Invoke(0, $"@panel:config target={_targetPanelTokens} overlap={_overlapTokens} maxOut={_maxOutputTokens} tpm={_tpm} singleWin={_singleWindowBudget} requireMCT={_requireMaxCompletionTokens}");
            }

            int totalTokens = CountTokens(text);

            // Single-window path if panelization disabled or small enough
            if (!_enablePanelization || totalTokens <= _singleWindowBudget)
                return await ChunkSingleWindowAsync(text, documentPath, onStream, ct);

            // Token-based windows irrespective of newlines
            var panels = BuildTokenPanels(text, _targetPanelTokens, _overlapTokens).ToList();
            var all = new List<ChunkRecord>(capacity: panels.Count * 3);

            // Pacing to avoid pre-generation 429s
            var baseSpacing = TimeSpan.FromSeconds(
                Math.Ceiling(((_targetPanelTokens + _maxOutputTokens) / (double)_tpm) * 60.0));
            var minSpacing = baseSpacing + TimeSpan.FromSeconds(1.0);
            DateTimeOffset nextEarliest = DateTimeOffset.UtcNow;

            for (int i = 0; i < panels.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var now = DateTimeOffset.UtcNow;
                if (now < nextEarliest) await Task.Delay(nextEarliest - now, ct);

                var pane = panels[i];

                // lifecycle marker: starting panel (token count known exactly)
                onStream?.Invoke(0, $"@panel:start {i + 1}/{panels.Count} in={pane.TokenCount} out={_maxOutputTokens}");

                // messages per panel
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(
                        "You split THIS WINDOW ONLY into semantically coherent chunks (~400–600 tokens). " +
                        "Return ONLY the chunk texts separated by the exact line '---CHUNK---'. " +
                        "Do not include any text that is not inside this window."),
                    new UserChatMessage($"[Window {i+1}/{panels.Count}] Begin window text below:\n{pane.Text}\n[End of window]")
                };

                var options = new ChatCompletionOptions();
                TrySetMaxTokens(options, _maxOutputTokens, onStream);

                // Optional prompt dump
                if (!string.IsNullOrWhiteSpace(_promptDumpDir))
                {
                    Directory.CreateDirectory(_promptDumpDir!);
                    var safe = TraceLog.MakeFileSafe((documentPath ?? "doc") + $".p{i:00}");
                    var path = Path.Combine(_promptDumpDir!, $"{safe}.prompt.json");
                    var dto = new
                    {
                        kind = "chunk_prompt",
                        doc = documentPath,
                        window = $"{i + 1}/{panels.Count}",
                        utc = DateTime.UtcNow,
                        messages = new object[]
                        {
                            new { role = "system", content = "You split THIS WINDOW ONLY into coherent chunks (~400–600 tokens). Return ONLY the chunk texts separated by the exact line '---CHUNK---'." },
                            new { role = "user", content = $"[Window {i+1}/{panels.Count}] Begin text:\n{pane.Text}\n[End]" }
                        }
                    };
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }), ct);
                }

                // streaming
                var sb = new StringBuilder(4096);
                var lastTick = Environment.TickCount;
                bool accepted = false;
                StreamWriter? dump = null;

                try
                {
                    if (!string.IsNullOrWhiteSpace(_streamDumpDir))
                    {
                        Directory.CreateDirectory(_streamDumpDir!);
                        var safe = TraceLog.MakeFileSafe((documentPath ?? "doc") + $".p{i:00}");
                        var dumpPath = Path.Combine(_streamDumpDir!, $"{safe}.chunk-stream.txt");
                        dump = new StreamWriter(new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                        await dump.WriteLineAsync($"# stream for: {documentPath} [panel {i + 1}/{panels.Count}]");
                        await dump.WriteLineAsync($"# utc start: {DateTime.UtcNow:O}");
                    }

                    var stream = _chat.CompleteChatStreamingAsync(messages, options, ct);
                    await foreach (var update in stream.WithCancellation(ct))
                    {
                        foreach (var part in update.ContentUpdate)
                        {
                            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                            {
                                sb.Append(part.Text);
                                if (dump is not null) await dump.WriteAsync(part.Text);
                                if (!accepted)
                                {
                                    accepted = true;
                                    onStream?.Invoke(sb.Length, $"@panel:accepted {i + 1}/{panels.Count}");
                                }
                            }
                        }

                        if (_streamEnabled && onStream is not null)
                        {
                            var nowTick = Environment.TickCount;
                            if (nowTick - lastTick >= _streamUpdateIntervalMs)
                            {
                                lastTick = nowTick;
                                onStream.Invoke(sb.Length, $"[p {i + 1}/{panels.Count}] {Tail(sb, _streamPreviewChars)}");
                                if (dump is not null) await dump.FlushAsync();
                            }
                        }
                    }

                    onStream?.Invoke(sb.Length, $"@panel:done {i + 1}/{panels.Count} chars={sb.Length}");
                    if (dump is not null)
                    {
                        await dump.WriteLineAsync();
                        await dump.WriteLineAsync($"# utc end: {DateTime.UtcNow:O}");
                        await dump.FlushAsync();
                    }
                }
                finally
                {
                    if (dump is not null) await dump.DisposeAsync();
                }

                var panelChunks = ParseChunks(sb.ToString(), documentPath);
                StitchInPlace(all, panelChunks);

                // schedule next call
                nextEarliest = DateTimeOffset.UtcNow + minSpacing;
            }

            // Reindex chunk indices (0..N-1)
            for (int i = 0; i < all.Count; i++)
                all[i] = all[i] with { Index = i };

            return all;
        }

        // === Single-window path ===
        private async Task<IReadOnlyList<ChunkRecord>> ChunkSingleWindowAsync(
            string text, string? documentPath, Action<int, string>? onStream, CancellationToken ct)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You split the input into semantically coherent chunks (~400–600 tokens). " +
                    "Return ONLY the chunks separated by the exact line '---CHUNK---' (no numbering, no extra text)."),
                new UserChatMessage(text)
            };

            var options = new ChatCompletionOptions();
            TrySetMaxTokens(options, _maxOutputTokens, onStream);

            if (!_streamEnabled || onStream is null)
            {
                var result = await _chat.CompleteChatAsync(messages, options, ct);
                var content = result.Value.Content?.FirstOrDefault()?.Text ?? string.Empty;
                return ParseChunks(content, documentPath);
            }

            // streaming
            var stream = _chat.CompleteChatStreamingAsync(messages, options, ct);
            var sb = new StringBuilder(8192);
            var lastTick = Environment.TickCount;
            StreamWriter? dump2 = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(_streamDumpDir))
                {
                    Directory.CreateDirectory(_streamDumpDir!);
                    var safe = TraceLog.MakeFileSafe(documentPath ?? $"doc-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                    var dumpPath = Path.Combine(_streamDumpDir!, $"{safe}.chunk-stream.txt");
                    dump2 = new StreamWriter(new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                    await dump2.WriteLineAsync($"# stream for: {documentPath}");
                    await dump2.WriteLineAsync($"# utc start: {DateTime.UtcNow:O}");
                }

                await foreach (var update in stream.WithCancellation(ct))
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                        {
                            sb.Append(part.Text);
                            if (dump2 is not null) await dump2.WriteAsync(part.Text);
                        }
                    }

                    var now = Environment.TickCount;
                    if (now - lastTick >= _streamUpdateIntervalMs)
                    {
                        lastTick = now;
                        onStream.Invoke(sb.Length, $"[p 1/1] {Tail(sb, _streamPreviewChars)}");
                        if (dump2 is not null) await dump2.FlushAsync();
                    }
                }

                if (sb.Length > 0)
                    onStream.Invoke(sb.Length, $"[p 1/1] {Tail(sb, _streamPreviewChars)}");

                if (dump2 is not null)
                {
                    await dump2.WriteLineAsync();
                    await dump2.WriteLineAsync($"# utc end: {DateTime.UtcNow:O}");
                    await dump2.FlushAsync();
                }
            }
            finally
            {
                if (dump2 is not null) await dump2.DisposeAsync();
            }

            return ParseChunks(sb.ToString(), documentPath);
        }

        // --- Token panels ---
        private sealed record TokenPanel(string Text, int TokenCount);

        private static IEnumerable<TokenPanel> BuildTokenPanels(string text, int targetTokens, int overlapTokens)
        {
            List<int> tokens = Enc.Encode(text);
            if (tokens.Count == 0) yield break;

            if (targetTokens <= 0) targetTokens = 1000;
            if (overlapTokens < 0) overlapTokens = 0;
            if (overlapTokens >= targetTokens) overlapTokens = Math.Max(0, targetTokens / 5);

            int start = 0;
            while (start < tokens.Count)
            {
                int end = Math.Min(start + targetTokens, tokens.Count);
                List<int> slice = tokens.GetRange(start, end - start);
                string panelText = Enc.Decode(slice);
                yield return new TokenPanel(panelText, slice.Count);
                if (end == tokens.Count) break;
                start = Math.Max(0, end - overlapTokens);
            }
        }

        /// <summary>
        /// Tries to set a completion/output token cap on ChatCompletionOptions in a way
        /// that works across SDK versions and model families.
        /// </summary>
        private bool TrySetMaxTokens(ChatCompletionOptions options, int? tokens, Action<int, string>? onStream)
        {
            // Prefer "MaxCompletionTokens" if present (newer SDKs / GPT-5)
            var t = options.GetType();
            var pNew = t.GetProperty("MaxCompletionTokens");
            if (pNew is not null)
            {
                pNew.SetValue(options, tokens);
                if (_emitPanelInfo) onStream?.Invoke(0, "@panel:info using MaxCompletionTokens");
                return true;
            }

            // Older SDKs: MaxOutputTokenCount; GPT-5 may reject its wire mapping ('max_tokens')
            if (_requireMaxCompletionTokens)
            {
                if (_emitPanelInfo) onStream?.Invoke(0, "@panel:info omitting max tokens (GPT-5 + SDK lacks MaxCompletionTokens)");
                return false; // leave unset to avoid 400 unsupported_parameter
            }

            options.MaxOutputTokenCount = tokens;
            if (_emitPanelInfo) onStream?.Invoke(0, "@panel:info using MaxOutputTokenCount");
            return true;
        }

        // === Helpers ===
        private static int CountTokens(string s) => Enc.CountTokens(s);

        private static List<ChunkRecord> ParseChunks(string content, string? documentPath)
        {
            var parts = content.Split("\n---CHUNK---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var chunks = new List<ChunkRecord>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
                chunks.Add(new ChunkRecord(documentPath ?? "", i, parts[i], Array.Empty<float>()));
            return chunks;
        }

        private static void StitchInPlace(List<ChunkRecord> acc, List<ChunkRecord> next)
        {
            if (next.Count == 0) return;
            if (acc.Count == 0) { acc.AddRange(next); return; }

            foreach (var ch in next)
            {
                if (acc.Count > 0 && NearlySame(acc[^1].Content, ch.Content))
                    continue;
                acc.Add(ch);
            }

            static bool NearlySame(string a, string b)
            {
                string Normalize(string s) => s.Trim().Replace("\r", "").Replace("\n", " ").ToLowerInvariant();
                var na = Normalize(a);
                var nb = Normalize(b);
                if (na.Length == 0 || nb.Length == 0) return na.Length == nb.Length;
                return na.Contains(nb) || nb.Contains(na) || (2.0 * Lcs(na, nb) / Math.Max(na.Length, nb.Length)) >= 0.85;
            }

            static int Lcs(string a, string b)
            {
                var dp = new int[a.Length + 1, b.Length + 1];
                for (int i = 1; i <= a.Length; i++)
                    for (int j = 1; j <= b.Length; j++)
                        dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
                return dp[a.Length, b.Length];
            }
        }

        private static string Tail(StringBuilder sb, int maxChars)
        {
            if (sb.Length <= maxChars) return sb.ToString();
            return "…" + sb.ToString(sb.Length - maxChars, maxChars);
        }
    }
}
