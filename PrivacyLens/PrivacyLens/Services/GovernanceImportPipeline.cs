// Services/GovernanceImportPipeline.cs
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Diagnostics;
using PrivacyLens.Models;

// Extraction libs
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

// Azure SDK v2 exception base (needed for 429/Retry-After handling)
using System.ClientModel; // ClientResultException

// Tokenizer
using SharpToken;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Orchestrates governance ingestion: extract → chunk (GPT) → embed → save.
    /// - Reports progress via ImportProgress (console UI shows stage, percent, 429 countdown, etc.)
    /// - Handles Azure throttling (429/503) with Retry-After and exponential backoff
    /// - Persists per-document trace files under .diagnostics\traces\*.log
    /// - Displays panel lifecycle markers from the chunker for granular visibility
    /// </summary>
    public sealed class GovernanceImportPipeline
    {
        private readonly IChunkingService _chunking;
        private readonly IEmbeddingService _embeddings;
        private readonly IVectorStore _store;
        private readonly ILogger<GovernanceImportPipeline>? _logger;

        // Diagnostics + throttling knobs
        private readonly bool _verbose;
        private readonly bool _showStageDurations;
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly int _maxDelayMs;
        private readonly int _jitterMs;
        private readonly int _minDelayBetweenRequestsMs;

        private string _folderPath = string.Empty;
        public string DefaultFolderPath => _folderPath;

        // Cache tokenizer (o200k_base via gpt-4o)
        private static readonly GptEncoding Tok = GptEncoding.GetEncodingForModel("gpt-4o");

        public GovernanceImportPipeline(
            IChunkingService chunking,
            IEmbeddingService embeddings,
            IVectorStore store)
        {
            _chunking = chunking ?? throw new ArgumentNullException(nameof(chunking));
            _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public GovernanceImportPipeline(
            IChunkingService chunking,
            IEmbeddingService embeddings,
            IVectorStore store,
            IConfiguration config,
            ILogger<GovernanceImportPipeline>? logger = null)
            : this(chunking, embeddings, store)
        {
            _logger = logger;

            // Default folder (support either key path)
            _folderPath = config["Ingestion:DefaultFolderPath"]
                          ?? config["PrivacyLens:Paths:SourceDocuments"]
                          ?? _folderPath;
            if (!string.IsNullOrWhiteSpace(_folderPath) && !Path.IsPathRooted(_folderPath))
                _folderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _folderPath));

            // Diagnostics
            _verbose = config.GetValue<bool>("Diagnostics:Verbose", false);
            _showStageDurations = config.GetValue<bool>("Diagnostics:ShowStageDurations", false);

            // Retry / throttling
            _maxRetries = config.GetValue<int>("AzureOpenAI:Retry:MaxRetries", 6);
            _baseDelayMs = config.GetValue<int>("AzureOpenAI:Retry:BaseDelayMs", 1500);
            _maxDelayMs = config.GetValue<int>("AzureOpenAI:Retry:MaxDelayMs", 60000);
            _jitterMs = config.GetValue<int>("AzureOpenAI:Retry:JitterMs", 250);

            // Smooth per-request spacing when embedding many chunks
            _minDelayBetweenRequestsMs = config.GetValue<int>("AzureOpenAI:MinDelayBetweenRequestsMs", 0);
        }

        public void SetFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                throw new DirectoryNotFoundException($"Folder not found: {path}");
            _folderPath = path;
        }

        public async Task ImportAllAsync(IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_folderPath))
                throw new InvalidOperationException("DefaultFolderPath is not set.");

            var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
                .Where(HasSupportedExtension)
                .ToArray();

            var total = files.Length;
            _logger?.LogInformation("Importing {Count} files from {Folder}", total, _folderPath);

            for (int i = 0; i < total; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                progress?.Report(new ImportProgress(i + 1, total, name, "Start"));

                try
                {
                    await ImportAsync(file, progress, i + 1, total, ct);
                    progress?.Report(new ImportProgress(i + 1, total, name, "Done"));
                }
                catch (Exception ex)
                {
                    progress?.Report(new ImportProgress(i + 1, total, name, "Error", ex.Message));
                    throw;
                }
            }
        }

        public async Task ImportAsync(
            string filePath,
            IProgress<ImportProgress>? progress = null,
            int current = 1,
            int total = 1,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            var name = Path.GetFileName(filePath);

            // Per-doc trace file
            var safeName = TraceLog.MakeFileSafe(name);
            var tracesDir = Path.Combine(AppContext.BaseDirectory, ".diagnostics", "traces");
            await using var trace = new TraceLog(tracesDir, safeName);
            await trace.InitAsync($"ingestion trace for {name}");

            // 1) Extract
            var sw = Stopwatch.StartNew();
            progress?.Report(new ImportProgress(current, total, name, "Extract"));
            var text = ExtractTextFromFile(filePath);
            sw.Stop();
            await trace.WriteLineAsync($"EXTRACT ok chars={text.Length:N0} tok={CountTokens(text):N0}");
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Extract", StageElapsedMs: sw.ElapsedMilliseconds));

            // 2) Chunk with retry/backoff (429-aware) + streaming preview
            sw.Restart();
            var approxPromptTok = CountTokens(text);
            progress?.Report(new ImportProgress(current, total, name, "Chunk", $"~{approxPromptTok:N0} prompt tok (est)"));

            // Stream callback → tidy progress line
            Action<int, string> onStream = (chars, preview) =>
            {
                if (string.IsNullOrEmpty(preview))
                    return;

                // Panel lifecycle markers
                if (preview.StartsWith("@panel:"))
                {
                    var status = preview.Substring("@panel:".Length).Trim();

                    // Suppress noisy informational hints from the chunker in the main UI,
                    // but keep them in the trace for diagnostics.
                    if (status.StartsWith("info ", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = trace.WriteLineAsync($"CHUNK {status} (chars={chars})");
                        return;
                    }

                    // Show start/accepted/done in the UI
                    progress?.Report(new ImportProgress(current, total, name, "Chunk", status));
                    _ = trace.WriteLineAsync($"CHUNK {status} (chars={chars})");
                    return;
                }

                // Default streaming tail: single-line, de-noised
                var clean = SanitizeProgress(preview);
                if (string.IsNullOrEmpty(clean))
                    return;

                var info = $"out {chars:N0} ch  {clean}";
                progress?.Report(new ImportProgress(current, total, name, "Chunk", info));
                _ = trace.WriteLineAsync($"CHUNK stream chars={chars} tail=\"{clean}\"");
            };

            IReadOnlyList<ChunkRecord> chunks = await ExecuteWithRetryAsync(
                async () => await _chunking.ChunkAsync(text, filePath, onStream, ct),
                onWait: (seconds, attempt, op) =>
                    progress?.Report(new ImportProgress(
                        current, total, name, "429 wait", $"{seconds}s ({op} retry {attempt}/{_maxRetries})")),
                opName: "chunk",
                ct: ct,
                trace: trace);

            sw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Chunk", StageElapsedMs: sw.ElapsedMilliseconds));

            // 3) Embed + Save
            sw.Restart();
            progress?.Report(new ImportProgress(current, total, name, "Embed+Save"));

            var materialized = new List<ChunkRecord>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Smooth embedding calls to avoid RPM micro-bursts
                if (_minDelayBetweenRequestsMs > 0 && i > 0)
                    await Task.Delay(_minDelayBetweenRequestsMs, ct);

                var ch = chunks[i];

                float[] embedding = await ExecuteWithRetryAsync(
                    () => _embeddings.EmbedAsync(ch.Content, ct),
                    onWait: (seconds, attempt, op) =>
                        progress?.Report(new ImportProgress(
                            current, total, name, "429 wait", $"{seconds}s ({op} retry {attempt}/{_maxRetries})")),
                    opName: "embedding",
                    ct: ct,
                    trace: trace);

                materialized.Add(ch with { Embedding = embedding });
            }

            await _store.InitializeAsync(ct);
            await _store.SaveChunksAsync(materialized, ct);

            sw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Embed+Save", StageElapsedMs: sw.ElapsedMilliseconds));
        }

        // ---------------------------- Retry/backoff core ----------------------------

        private async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            Action<int, int, string>? onWait,
            string opName,
            CancellationToken ct,
            TraceLog? trace = null)
        {
            int attempt = 0;
            var delay = TimeSpan.FromMilliseconds(_baseDelayMs);
            var rng = _jitterMs > 0 ? new Random() : null;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await action();
                }
                catch (ClientResultException ex)
                {
                    var (status, retryAfterSec) = TryGetStatusAndRetryAfter(ex);
                    if (status == 429 || status == 503)
                    {
                        attempt++;
                        if (attempt > _maxRetries) throw;

                        var wait = retryAfterSec.HasValue
                            ? TimeSpan.FromSeconds(retryAfterSec.Value)
                            : delay;

                        if (rng != null)
                        {
                            var jitter = rng.Next(-_jitterMs, _jitterMs + 1);
                            wait = wait + TimeSpan.FromMilliseconds(jitter);
                        }

                        var (rt, lt, rr, lr, rid) = TryGetRateHeaders(ex);
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(rt) && !string.IsNullOrEmpty(lt)) parts.Add($"TPM {rt}/{lt}");
                        if (!string.IsNullOrEmpty(rr) && !string.IsNullOrEmpty(lr)) parts.Add($"RPM {rr}/{lr}");
                        if (!string.IsNullOrEmpty(rid)) parts.Add($"rid={rid}");
                        var hdr = parts.Count > 0 ? ", " + string.Join(" ", parts) : "";
                        var info = $"{opName}{hdr} retry {attempt}/{_maxRetries} wait={wait.TotalSeconds:N0}s";

                        if (_verbose)
                            _logger?.LogWarning("{Info}", info);

                        onWait?.Invoke((int)Math.Max(1, wait.TotalSeconds), attempt, $"{opName}{hdr}");
                        if (trace is not null)
                            await trace.WriteLineAsync($"THROTTLE {info}");

                        await Task.Delay(wait, ct);

                        // Exponential backoff only if Retry-After not provided
                        if (!retryAfterSec.HasValue)
                        {
                            var nextMs = Math.Min((int)(delay.TotalMilliseconds * 2), _maxDelayMs);
                            delay = TimeSpan.FromMilliseconds(nextMs);
                        }

                        continue;
                    }

                    // non-throttling error
                    if (trace is not null)
                        await trace.WriteLineAsync($"ERROR {opName} status={status} ex={ex.Message}");
                    throw;
                }
            }
        }

        private static (int Status, int? RetryAfterSec) TryGetStatusAndRetryAfter(ClientResultException ex)
        {
            try
            {
                var resp = ex.GetRawResponse();
                int status = resp?.Status ?? 0;
                int? retryAfter = null;

                if (resp?.Headers.TryGetValue("Retry-After", out var ra) == true)
                {
                    if (int.TryParse(ra, out var s)) retryAfter = s;
                }
                else
                {
                    // Fallback: parse from message like "... retry after 60 seconds ..."
                    var msg = ex.Message;
                    var idx = msg.IndexOf("retry after", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var tail = msg.Substring(idx);
                        var digits = new string(tail.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out var s)) retryAfter = s;
                    }
                }
                return (status, retryAfter);
            }
            catch
            {
                return (0, null);
            }
        }

        private static (string? remainTokens, string? limitTokens, string? remainReq, string? limitReq, string? requestId)
        TryGetRateHeaders(ClientResultException ex)
        {
            try
            {
                var resp = ex.GetRawResponse();
                if (resp is null) return (null, null, null, null, null);
                resp.Headers.TryGetValue("x-ratelimit-remaining-tokens", out var rt);
                resp.Headers.TryGetValue("x-ratelimit-limit-tokens", out var lt);
                resp.Headers.TryGetValue("x-ratelimit-remaining-requests", out var rr);
                resp.Headers.TryGetValue("x-ratelimit-limit-requests", out var lr);
                resp.Headers.TryGetValue("apim-request-id", out var rid);
                return (rt, lt, rr, lr, rid);
            }
            catch { return (null, null, null, null, null); }
        }

        private static int CountTokens(string text) => Tok.CountTokens(text);

        // ---------------------------- Extraction helpers ----------------------------

        private static bool HasSupportedExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext is ".pdf" or ".docx" or ".pptx" or ".xlsx";
        }

        private static string ExtractTextFromFile(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".pdf" => ExtractPdf(filePath),
                ".docx" => ExtractDocx(filePath),
                ".pptx" => ExtractPptx(filePath),
                ".xlsx" => ExtractXlsx(filePath),
                var ext => throw new NotSupportedException($"Unsupported file type: {ext}")
            };
        }

        private static string ExtractPdf(string filePath)
        {
            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(filePath);
            foreach (var page in pdf.GetPages())
                sb.AppendLine(page.Text);
            return sb.ToString();
        }

        private static string ExtractDocx(string filePath)
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var p in body.Descendants<Paragraph>())
            {
                var text = p.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
            return sb.ToString();
        }

        private static string ExtractPptx(string filePath)
        {
            using var pres = PresentationDocument.Open(filePath, false);
            var presPart = pres.PresentationPart;
            if (presPart is null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var slidePart in presPart.SlideParts)
            {
                var texts = slidePart.Slide?
                    .Descendants<A.Text>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t));

                if (texts != null)
                {
                    foreach (var t in texts) sb.AppendLine(t);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string ExtractXlsx(string filePath)
        {
            using var ss = SpreadsheetDocument.Open(filePath, false);
            var wbPart = ss.WorkbookPart;
            if (wbPart is null) return string.Empty;

            var sb = new StringBuilder();
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;
            var sheets = wbPart.Workbook.Sheets?.OfType<Sheet>() ?? Enumerable.Empty<Sheet>();

            foreach (var sheet in sheets)
            {
                var relId = sheet.Id?.Value;
                if (string.IsNullOrEmpty(relId)) continue;

                var wsPart = (WorksheetPart)wbPart.GetPartById(relId);
                var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
                if (sheetData is null) continue;

                sb.AppendLine($"# Sheet: {sheet.Name}");
                foreach (var row in sheetData.Elements<Row>())
                {
                    var cellValues = new List<string>();
                    foreach (var cell in row.Elements<Cell>())
                        cellValues.Add(GetCellDisplayString(cell, wbPart, sst));

                    if (cellValues.Count > 0)
                        sb.AppendLine(string.Join('\t', cellValues));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string GetCellDisplayString(Cell cell, WorkbookPart wbPart, SharedStringTable? sst)
        {
            if (cell == null) return string.Empty;

            if (cell.DataType?.Value == CellValues.SharedString && sst != null)
            {
                var idxText = cell.CellValue?.InnerText;
                if (int.TryParse(idxText, out var idx) && idx >= 0)
                {
                    var si = sst.Elements<SharedStringItem>().ElementAtOrDefault(idx);
                    return si?.InnerText ?? string.Empty;
                }
                return string.Empty;
            }

            if (cell.DataType?.Value == CellValues.InlineString && cell.InlineString != null)
                return cell.InlineString.InnerText ?? string.Empty;

            if (cell.DataType?.Value == CellValues.Boolean)
                return (cell.CellValue?.InnerText == "1") ? "TRUE" : "FALSE";

            return cell.CellValue?.InnerText ?? string.Empty;
        }

        // ---------------------------- Utilities ----------------------------

        private static string SanitizeProgress(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            // Replace control chars with space (keep the line one-liner), then collapse whitespace
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsControl(c))
                {
                    if (c == '\n' || c == '\r' || c == '\t') sb.Append(' ');
                    // else drop other control chars
                }
                else
                {
                    sb.Append(c);
                }
            }

            var oneLine = sb.ToString();
            var collapsed = Regex.Replace(oneLine, @"\s+", " ").Trim();

            // Clip very long tails just in case
            const int max = 200;
            return (collapsed.Length <= max) ? collapsed : collapsed.Substring(0, max) + " …";
        }
    }
}
