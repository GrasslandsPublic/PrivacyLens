// Services/CorporateScrapeImporter.cs - Enhanced with file validation
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PrivacyLens.Chunking;
using PrivacyLens.Models;
using PrivacyLens.Services;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enhanced importer for scrape folders with comprehensive file validation:
    ///   • HTML in /webpages → DOM-segment + hybrid chunking → embed + save
    ///   • Docs in /documents → validate → pipeline.ImportAsync
    ///   • Invalid files → quarantine with detailed logging
    /// </summary>
    public sealed class CorporateScrapeImporter
    {
        private readonly GovernanceImportPipeline _pipeline;
        private readonly HybridChunkingOrchestrator _htmlOrchestrator;
        private readonly EmbeddingService _embed;
        private readonly VectorStore _store;
        private readonly IConfiguration _config;

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;

            // HTML hybrid chunking components
            var boiler = new SimpleBoilerplateFilter();
            var seg = new HtmlDomSegmenter(boiler);
            var simple = new SimpleTextChunker();
            var gpt = new GptChunkingService(config);
            _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);

            // Embedding + Vector store for HTML path
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
        }

        public async Task ImportScrapeAsync(string scrapeRoot, CancellationToken ct = default)
        {
            var pagesDir = Path.Combine(scrapeRoot, "webpages");
            var docsDir = Path.Combine(scrapeRoot, "documents");
            var quarantineDir = Path.Combine(scrapeRoot, "_quarantine");
            Directory.CreateDirectory(quarantineDir);

            // Create validation report file
            var validationReportPath = Path.Combine(scrapeRoot, "validation_report.txt");
            using var reportWriter = new StreamWriter(validationReportPath);
            await reportWriter.WriteLineAsync($"=== Scrape Import Validation Report ===");
            await reportWriter.WriteLineAsync($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await reportWriter.WriteLineAsync($"Scrape Root: {scrapeRoot}\n");

            int htmlOk = 0, htmlErr = 0;
            int docOk = 0, docSkip = 0, docErr = 0, docQuarantined = 0;

            Console.WriteLine("Analyzing scrape folders...\n");

            var htmlFiles = Directory.Exists(pagesDir)
                ? Directory.GetFiles(pagesDir, "*.html", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            var docFiles = Directory.Exists(docsDir)
                ? Directory.GetFiles(docsDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<string>();

            Console.WriteLine($" 📄 HTML pages : {htmlFiles.Length}");
            Console.WriteLine($" 📁 Documents  : {docFiles.Length}");
            Console.WriteLine();

            // ===== HTML IMPORT =====
            if (htmlFiles.Length > 0)
            {
                Console.WriteLine("Importing HTML pages (hybrid chunking):\n");
                await reportWriter.WriteLineAsync("=== HTML Pages ===");

                for (int i = 0; i < htmlFiles.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = htmlFiles[i];
                    var name = Path.GetFileName(file);

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{i + 1}/{htmlFiles.Length}] (HTML) {name}");
                    Console.ResetColor();

                    try
                    {
                        var html = await File.ReadAllTextAsync(file, ct);

                        // Basic HTML validation
                        if (string.IsNullOrWhiteSpace(html) || html.Length < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠️ Skipping: HTML too short ({html.Length} chars)");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"SKIP: {name} - Too short ({html.Length} chars)");
                            continue;
                        }

                        // Chunk HTML using hybrid strategy
                        var chunkRecords = await _htmlOrchestrator.ChunkHtmlAsync(html, file, ct);
                        Console.WriteLine($"  • Sections/chunks: {chunkRecords.Count}");

                        // Embed each chunk and save
                        var materialized = new List<ChunkRecord>(chunkRecords.Count);
                        for (int c = 0; c < chunkRecords.Count; c++)
                        {
                            var ch = chunkRecords[c];
                            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

                            var emb = await _embed.EmbedAsync(ch.Content, ct);
                            materialized.Add(ch with { Embedding = emb });

                            if ((c + 1) % Math.Max(1, chunkRecords.Count / 10) == 0 || c == chunkRecords.Count - 1)
                            {
                                Console.WriteLine($"    - Embedded {c + 1}/{chunkRecords.Count}");
                            }
                        }

                        await _store.SaveChunksAsync(materialized, ct);
                        Console.WriteLine("  ✅ Saved chunks to database");
                        await reportWriter.WriteLineAsync($"OK: {name} - {chunkRecords.Count} chunks");
                        htmlOk++;
                    }
                    catch (Exception ex)
                    {
                        htmlErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ❌ HTML import failed: {ex.Message}");
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"ERROR: {name} - {ex.Message}");
                    }
                }

                Console.WriteLine();
            }

            // ===== DOCUMENT IMPORT with validation =====
            if (docFiles.Length > 0)
            {
                Console.WriteLine("Importing downloaded documents:\n");
                await reportWriter.WriteLineAsync("\n=== Documents ===");

                for (int i = 0; i < docFiles.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = docFiles[i];
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToLowerInvariant();

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{i + 1}/{docFiles.Length}] (DOC) {name}");
                    Console.ResetColor();

                    try
                    {
                        // File size validation
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length == 0)
                        {
                            docQuarantined++;
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠️ Quarantining: Empty file (0 bytes)");
                            Console.ResetColor();

                            await QuarantineFileAsync(file, quarantineDir, "empty_file");
                            await reportWriter.WriteLineAsync($"QUARANTINE: {name} - Empty file");
                            continue;
                        }

                        // Validate file based on extension and magic bytes
                        bool isValid = false;
                        string? detectedType = null;

                        if (!string.IsNullOrEmpty(ext))
                        {
                            isValid = FileValidator.IsValid(file, ext);
                            if (!isValid)
                            {
                                // Try to detect actual type
                                detectedType = FileValidator.DetectFileType(file);
                                if (!string.IsNullOrEmpty(detectedType))
                                {
                                    Console.WriteLine($"  ℹ️ File type mismatch: expected {ext}, detected {detectedType}");

                                    // If it's a valid document type, rename and proceed
                                    if (IsDocumentExtension(detectedType))
                                    {
                                        var newPath = Path.ChangeExtension(file, detectedType);
                                        if (!File.Exists(newPath))
                                        {
                                            File.Move(file, newPath);
                                            file = newPath;
                                            name = Path.GetFileName(file);
                                            ext = detectedType;
                                            isValid = true;
                                            Console.WriteLine($"  ✅ Renamed to: {name}");
                                        }
                                    }
                                }
                            }
                        }

                        // Special handling for PDFs
                        if (ext == ".pdf" && !FileValidator.IsValidPdf(file))
                        {
                            docQuarantined++;
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠️ Quarantining: Invalid PDF (bad signature)");
                            Console.ResetColor();

                            await QuarantineFileAsync(file, quarantineDir, "invalid_pdf");
                            await reportWriter.WriteLineAsync($"QUARANTINE: {name} - Invalid PDF signature");
                            continue;
                        }

                        // Skip HTML files that ended up in documents folder
                        if (ext == ".html" || ext == ".htm")
                        {
                            docSkip++;
                            Console.WriteLine($"  ℹ️ Skipping: HTML file in documents folder");
                            await reportWriter.WriteLineAsync($"SKIP: {name} - HTML file");
                            continue;
                        }

                        // Additional validation for Office documents
                        if (IsOfficeDocument(ext))
                        {
                            if (!FileValidator.IsValid(file, ext))
                            {
                                // Check if it's actually a corrupted download (common with HTML error pages)
                                var firstBytes = await ReadFirstBytesAsync(file, 1000);
                                if (IsLikelyHtmlError(firstBytes))
                                {
                                    docQuarantined++;
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.WriteLine($"  ⚠️ Quarantining: HTML error page saved as {ext}");
                                    Console.ResetColor();

                                    await QuarantineFileAsync(file, quarantineDir, "html_error_as_document");
                                    await reportWriter.WriteLineAsync($"QUARANTINE: {name} - HTML error page");
                                    continue;
                                }
                            }
                        }

                        // If file passed all validations, import it
                        var progress = new ConsoleProgress(name);
                        await _pipeline.ImportAsync(file, progress, i + 1, docFiles.Length, ct);
                        await reportWriter.WriteLineAsync($"OK: {name} - Imported successfully");
                        docOk++;
                    }
                    catch (InvalidDataException idEx)
                    {
                        docQuarantined++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⚠️ Invalid data: {idEx.Message}. Moving to quarantine.");
                        Console.ResetColor();

                        await QuarantineFileAsync(file, quarantineDir, "invalid_data");
                        await reportWriter.WriteLineAsync($"QUARANTINE: {name} - {idEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        docErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ❌ DOC import failed: {ex.Message}");
                        Console.ResetColor();

                        // Quarantine problematic files
                        await QuarantineFileAsync(file, quarantineDir, "import_error");
                        await reportWriter.WriteLineAsync($"ERROR: {name} - {ex.Message}");
                    }
                }

                Console.WriteLine();
            }

            // ===== SUMMARY =====
            var summary = new[]
            {
                "========================================",
                " Import Summary",
                "========================================",
                $" HTML:  OK={htmlOk}, ERR={htmlErr}",
                $" DOCS:  OK={docOk}, SKIP={docSkip}, ERR={docErr}, QUARANTINED={docQuarantined}",
                $" Quarantine: {Directory.GetFiles(quarantineDir, "*", SearchOption.TopDirectoryOnly).Length} file(s)",
                "========================================"
            };

            foreach (var line in summary)
            {
                Console.WriteLine(line);
                await reportWriter.WriteLineAsync(line);
            }

            Console.WriteLine();
            await reportWriter.WriteLineAsync($"\nCompleted: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        private static async Task QuarantineFileAsync(string sourcePath, string quarantineDir, string reason)
        {
            try
            {
                var fileName = Path.GetFileName(sourcePath);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var quarantineName = $"{Path.GetFileNameWithoutExtension(fileName)}_{reason}_{timestamp}{Path.GetExtension(fileName)}";
                var destPath = Path.Combine(quarantineDir, quarantineName);

                // Move the file
                File.Move(sourcePath, destPath);

                // Create a metadata file
                var metaPath = Path.ChangeExtension(destPath, ".quarantine.json");
                var metadata = new
                {
                    OriginalPath = sourcePath,
                    OriginalName = fileName,
                    QuarantineReason = reason,
                    QuarantineTime = DateTime.UtcNow,
                    FileSize = new FileInfo(destPath).Length
                };

                await File.WriteAllTextAsync(metaPath,
                    System.Text.Json.JsonSerializer.Serialize(metadata,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️ Failed to quarantine file: {ex.Message}");
            }
        }

        private static bool IsDocumentExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            if (!ext.StartsWith(".")) ext = "." + ext;

            var docExtensions = new[] {
                ".pdf", ".doc", ".docx", ".xls", ".xlsx",
                ".ppt", ".pptx", ".txt", ".csv", ".rtf", ".zip"
            };

            return docExtensions.Contains(ext.ToLowerInvariant());
        }

        private static bool IsOfficeDocument(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            if (!ext.StartsWith(".")) ext = "." + ext;

            var officeExtensions = new[] {
                ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
            };

            return officeExtensions.Contains(ext.ToLowerInvariant());
        }

        private static async Task<byte[]> ReadFirstBytesAsync(string filePath, int count)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var buffer = new byte[Math.Min(count, fs.Length)];
                await fs.ReadAsync(buffer, 0, buffer.Length);
                return buffer;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static bool IsLikelyHtmlError(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            var text = System.Text.Encoding.UTF8.GetString(bytes).ToLowerInvariant();

            // Common HTML error indicators
            var errorIndicators = new[]
            {
                "<!doctype html",
                "<html",
                "404",
                "403",
                "error",
                "not found",
                "forbidden",
                "unauthorized",
                "access denied"
            };

            return errorIndicators.Any(indicator => text.Contains(indicator));
        }

        /// <summary>
        /// Console progress reporter that prints pipeline stages
        /// </summary>
        private sealed class ConsoleProgress : IProgress<ImportProgress>
        {
            private readonly string _name;
            private DateTime _last = DateTime.MinValue;

            public ConsoleProgress(string name) => _name = name;

            public void Report(ImportProgress p)
            {
                // Throttle very chatty updates
                if ((DateTime.Now - _last).TotalMilliseconds < 75) return;
                _last = DateTime.Now;

                var info = string.IsNullOrWhiteSpace(p.Info) ? "" : $" :: {p.Info}";
                Console.WriteLine($"  [{p.Current}/{p.Total}] {_name} :: {p.Stage}{info}");
            }
        }
    }
}