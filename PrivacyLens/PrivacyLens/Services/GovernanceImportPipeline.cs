// Services/GovernanceImportPipeline.cs — Patched
// - Adds ImportTextAsync for direct text ingestion with progress
// - Adds IsLikelyPdfFile() and PDF signature check in ExtractPdf()
// - Keeps existing GPT panelized chunking pipeline for files
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
// Azure SDK v2 exception base
using System.ClientModel; // ClientResultException
// Tokenizer
using SharpToken;

namespace PrivacyLens.Services
{
    public sealed class GovernanceImportPipeline
    {
        private readonly IChunkingService _chunking;
        private readonly IEmbeddingService _embeddings;
        private readonly IVectorStore _store;
        private readonly ILogger<GovernanceImportPipeline>? _logger;
        private readonly bool _verbose;
        private readonly bool _showStageDurations;
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly int _maxDelayMs;
        private readonly int _jitterMs;
        private readonly int _minDelayBetweenRequestsMs;
        // Optional fixed dimension from config (VectorStore:DesiredEmbeddingDim)
        private readonly int? _fixedDim;
        private string _folderPath = string.Empty;
        public string DefaultFolderPath => _folderPath;

        private static readonly GptEncoding Tok = GptEncoding.GetEncodingForModel("gpt-4o");

        public GovernanceImportPipeline(
            IChunkingService chunking, IEmbeddingService embeddings, IVectorStore store)
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

            _folderPath = config["Ingestion:DefaultFolderPath"]
                ?? config["PrivacyLens:Paths:SourceDocuments"]
                ?? _folderPath;

            if (!string.IsNullOrWhiteSpace(_folderPath) && !Path.IsPathRooted(_folderPath))
                _folderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _folderPath));

            var diag = config.GetSection("AzureOpenAI:Diagnostics");
            _verbose = diag.GetValue("Verbose", false);
            _showStageDurations = diag.GetValue("ShowStageDurations", false);

            _maxRetries = config.GetValue<int>("AzureOpenAI:Retry:MaxRetries", 6);
            _baseDelayMs = config.GetValue<int>("AzureOpenAI:Retry:BaseDelayMs", 1500);
            _maxDelayMs = config.GetValue<int>("AzureOpenAI:Retry:MaxDelayMs", 60000);
            _jitterMs = config.GetValue<int>("AzureOpenAI:Retry:JitterMs", 250);
            _minDelayBetweenRequestsMs = config.GetValue<int>("AzureOpenAI:MinDelayBetweenRequestsMs", 0);

            _fixedDim = config.GetValue<int?>("VectorStore:DesiredEmbeddingDim");
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

        /// <summary>
        /// Import raw text content (e.g., cleaned HTML or aggregated text) and persist chunks.
        /// </summary>
        public async Task ImportTextAsync(
            string text,
            string syntheticPath,
            IProgress<ImportProgress>? progress = null,
            int current = 1,
            int total = 1,
            CancellationToken ct = default)
        {
            var name = Path.GetFileName(syntheticPath);
            var safeName = TraceLog.MakeFileSafe(name);
            var tracesDir = Path.Combine(AppContext.BaseDirectory, ".diagnostics", "traces");
            await using var trace = new TraceLog(tracesDir, safeName);
            await trace.InitAsync($"ingestion text trace for {name}");

            // 1) Chunk
            var sw = Stopwatch.StartNew();
            progress?.Report(new ImportProgress(current, total, name, "Chunk"));
            IReadOnlyList<ChunkRecord> chunks = await _chunking.ChunkAsync(text, syntheticPath,
                onStream: (chars, preview) =>
                {
                    if (!string.IsNullOrEmpty(preview))
                        progress?.Report(new ImportProgress(current, total, name, "Chunk", SanitizeProgress(preview)));
                }, ct);
            sw.Stop();
            await trace.WriteLineAsync($"CHUNK ok chunks={chunks.Count:N0}");
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Chunk", StageElapsedMs: sw.ElapsedMilliseconds));

            // 2) Embed + Save
            progress?.Report(new ImportProgress(current, total, name, "Embed+Save", $"chunks={chunks.Count}"));
            var embedSw = Stopwatch.StartNew();
            var materialized = new List<ChunkRecord>(chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_minDelayBetweenRequestsMs > 0 && i > 0)
                    await Task.Delay(_minDelayBetweenRequestsMs, ct);

                var ch = chunks[i];
                var tok = CountTokens(ch.Content);
                var perSw = Stopwatch.StartNew();
                float[] embedding = await _embeddings.EmbedAsync(ch.Content, ct);
                perSw.Stop();

                if (_fixedDim.HasValue && embedding.Length != _fixedDim.Value)
                    throw new InvalidOperationException(
                        $"Embedding dimension mismatch: expected {_fixedDim.Value}, got {embedding.Length}. " +
                        $"Check your embedding deployment (e.g., use 'text-embedding-3-large' for 3072).");

                materialized.Add(ch with { Embedding = embedding });
                progress?.Report(new ImportProgress(
                    current, total, name, "Embed",
                    $"chunk {i + 1}/{chunks.Count} tok={tok} emb_dim={embedding.Length} {perSw.ElapsedMilliseconds}ms"));
            }

            embedSw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Embed", StageElapsedMs: embedSw.ElapsedMilliseconds));

            // Save
            var saveSw = Stopwatch.StartNew();
            progress?.Report(new ImportProgress(current, total, name, "Save", $"writing {materialized.Count} chunks..."));
            await _store.SaveChunksAsync(materialized, ct);
            saveSw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Save", StageElapsedMs: saveSw.ElapsedMilliseconds));
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

            // 2) Chunk
            sw.Restart();
            var approxPromptTok = CountTokens(text);
            progress?.Report(new ImportProgress(current, total, name, "Chunk", $"~{approxPromptTok:N0} prompt tok (est)"));

            Action<int, string> onStream = (chars, preview) =>
            {
                if (string.IsNullOrEmpty(preview)) return;
                if (preview.StartsWith("@panel:"))
                {
                    var status = preview.Substring("@panel:".Length).Trim();
                    if (!status.StartsWith("info ", StringComparison.OrdinalIgnoreCase))
                        progress?.Report(new ImportProgress(current, total, name, "Chunk", status));
                    return;
                }
                var clean = SanitizeProgress(preview);
                if (!string.IsNullOrEmpty(clean))
                    progress?.Report(new ImportProgress(current, total, name, "Chunk", clean));
            };

            IReadOnlyList<ChunkRecord> chunks = await _chunking.ChunkAsync(text, filePath, onStream, ct);
            sw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Chunk", StageElapsedMs: sw.ElapsedMilliseconds));

            // 3) Embed + Save with dimension guard
            progress?.Report(new ImportProgress(current, total, name, "Embed+Save", $"chunks={chunks.Count}"));
            var embedSw = Stopwatch.StartNew();
            var materialized = new List<ChunkRecord>(chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_minDelayBetweenRequestsMs > 0 && i > 0)
                    await Task.Delay(_minDelayBetweenRequestsMs, ct);

                var ch = chunks[i];
                var tok = CountTokens(ch.Content);
                var perSw = Stopwatch.StartNew();
                float[] embedding = await _embeddings.EmbedAsync(ch.Content, ct);
                perSw.Stop();

                if (_fixedDim.HasValue && embedding.Length != _fixedDim.Value)
                    throw new InvalidOperationException(
                        $"Embedding dimension mismatch: expected {_fixedDim.Value}, got {embedding.Length}. " +
                        $"Check your embedding deployment (e.g., use 'text-embedding-3-large' for 3072).");

                materialized.Add(ch with { Embedding = embedding });
                progress?.Report(new ImportProgress(
                    current, total, name, "Embed",
                    $"chunk {i + 1}/{chunks.Count} tok={tok} emb_dim={embedding.Length} {perSw.ElapsedMilliseconds}ms"));
            }

            embedSw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Embed", StageElapsedMs: embedSw.ElapsedMilliseconds));

            // Save with simple progress
            var saveSw = Stopwatch.StartNew();
            progress?.Report(new ImportProgress(current, total, name, "Save", $"writing {materialized.Count} chunks..."));
            await _store.SaveChunksAsync(materialized, ct);
            progress?.Report(new ImportProgress(current, total, name, "Save", $"saved {materialized.Count} chunks"));
            saveSw.Stop();
            if (_showStageDurations)
                progress?.Report(new ImportProgress(current, total, name, "Save", StageElapsedMs: saveSw.ElapsedMilliseconds));
        }

        private static int CountTokens(string text) => Tok.CountTokens(text);

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

        private static bool IsLikelyPdfFile(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                if (fs.Length < 6) return false;
                Span<byte> sig = stackalloc byte[5];
                _ = fs.Read(sig);
                return Encoding.ASCII.GetString(sig) == "%PDF-";
            }
            catch { return false; }
        }

        private static string ExtractPdf(string filePath)
        {
            if (!IsLikelyPdfFile(filePath))
                throw new InvalidDataException($"File is not a valid PDF (missing %PDF- header): {Path.GetFileName(filePath)}");

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

        private static string SanitizeProgress(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsControl(c))
                {
                    if (c == '\n' || c == '\r' || c == '\t') sb.Append(' ');
                }
                else sb.Append(c);
            }
            var oneLine = sb.ToString();
            var collapsed = Regex.Replace(oneLine, @"\s+", " ").Trim();
            const int max = 200;
            return (collapsed.Length <= max) ? collapsed : collapsed.Substring(0, max) + " …";
        }
    }
}
