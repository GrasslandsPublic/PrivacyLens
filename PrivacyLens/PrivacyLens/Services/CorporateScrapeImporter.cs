// Services/CorporateScrapeImporter.cs - Fixed with comprehensive debug logging
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<GptChunkingService> _logger;
        private readonly bool _debugMode;

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;

            // Check if debug mode is enabled
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Create logger
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_debugMode ? LogLevel.Debug : LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<GptChunkingService>();

            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("[DEBUG] Debug mode is ENABLED - verbose logging active");
                Console.ResetColor();
            }

            // HTML hybrid chunking components - Pass logger to GptChunkingService
            var boiler = new SimpleBoilerplateFilter();
            var seg = new HtmlDomSegmenter(boiler);
            var simple = new SimpleTextChunker();
            var gpt = new GptChunkingService(config, _logger);
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

            int htmlOk = 0, htmlErr = 0, htmlEmpty = 0;
            int docOk = 0, docSkip = 0, docErr = 0, docQuarantined = 0;

            Console.WriteLine("\n========================================");
            Console.WriteLine("SCRAPE IMPORT ANALYSIS");
            Console.WriteLine("========================================");
            Console.WriteLine($"Scrape Root: {scrapeRoot}");
            Console.WriteLine($"Debug Mode: {(_debugMode ? "ENABLED" : "Disabled")}");
            Console.WriteLine();

            var htmlFiles = Directory.Exists(pagesDir)
                ? Directory.GetFiles(pagesDir, "*.html", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            var docFiles = Directory.Exists(docsDir)
                ? Directory.GetFiles(docsDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<string>();

            Console.WriteLine($" 📄 HTML pages found: {htmlFiles.Length}");
            Console.WriteLine($" 📑 Documents found: {docFiles.Length}");
            Console.WriteLine();

            // Process HTML files
            if (htmlFiles.Length > 0)
            {
                Console.WriteLine("========================================");
                Console.WriteLine("PROCESSING HTML PAGES");
                Console.WriteLine("========================================");
                await reportWriter.WriteLineAsync("=== HTML PAGES ===");

                for (int i = 0; i < htmlFiles.Length; i++)
                {
                    var htmlPath = htmlFiles[i];
                    var name = Path.GetFileName(htmlPath);

                    Console.WriteLine($"\n[{i + 1}/{htmlFiles.Length}] Processing: {name}");

                    try
                    {
                        // Read HTML content
                        var content = await File.ReadAllTextAsync(htmlPath, ct);
                        var fileSize = new FileInfo(htmlPath).Length;

                        if (_debugMode)
                        {
                            Console.WriteLine($"  • File size: {fileSize:N0} bytes");
                            Console.WriteLine($"  • Content length: {content.Length:N0} chars");
                        }

                        // Check for minimum content
                        if (content.Length < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ SKIPPED: Too short ({content.Length} chars < 100)");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"SKIP: {name} - Too short ({content.Length} chars)");
                            htmlEmpty++;
                            continue;
                        }

                        // Chunk HTML using hybrid strategy
                        if (_debugMode)
                            Console.WriteLine($"  • Chunking HTML content...");

                        var chunks = await _htmlOrchestrator.ChunkHtmlAsync(content, htmlPath, ct);

                        if (chunks == null || chunks.Count == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ NO CHUNKS: Hybrid chunker returned 0 chunks");
                            Console.ResetColor();

                            if (_debugMode)
                            {
                                // Try to understand why
                                Console.WriteLine($"    - Checking for boilerplate removal...");
                                var firstPart = content.Length > 500 ? content.Substring(0, 500) : content;
                                Console.WriteLine($"    - First 500 chars: {firstPart}");
                            }

                            await reportWriter.WriteLineAsync($"EMPTY: {name} - No chunks generated");
                            htmlEmpty++;
                            continue;
                        }

                        Console.WriteLine($"  • Generated {chunks.Count} chunks");

                        // Embed and save each chunk
                        if (_debugMode)
                            Console.WriteLine($"  • Embedding chunks...");

                        var materialized = new List<ChunkRecord>();
                        for (int c = 0; c < chunks.Count; c++)
                        {
                            var ch = chunks[c];

                            if (_debugMode && c == 0) // Show first chunk details
                            {
                                var preview = ch.Content.Length > 100
                                    ? ch.Content.Substring(0, 100) + "..."
                                    : ch.Content;
                                Console.WriteLine($"    - Chunk 1 preview: {preview}");
                                Console.WriteLine($"    - Chunk 1 length: {ch.Content.Length} chars");
                            }

                            var embedding = await _embed.EmbedAsync(ch.Content, ct);
                            materialized.Add(ch with { Embedding = embedding });

                            // Progress indicator for large chunk sets
                            if (chunks.Count > 10 && (c + 1) % 10 == 0)
                            {
                                Console.WriteLine($"    - Embedded {c + 1}/{chunks.Count} chunks");
                            }
                        }

                        if (_debugMode)
                            Console.WriteLine($"  • Saving {materialized.Count} chunks to database...");

                        await _store.SaveChunksAsync(materialized, ct);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ SUCCESS: {name} → {chunks.Count} chunks saved");
                        Console.ResetColor();

                        await reportWriter.WriteLineAsync($"✓ {name} → {chunks.Count} chunks");
                        htmlOk++;
                    }
                    catch (Exception ex)
                    {
                        htmlErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ ERROR: {ex.Message}");
                        if (_debugMode)
                        {
                            Console.WriteLine($"    Stack trace: {ex.StackTrace}");
                        }
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"✗ {name}: {ex.Message}");
                    }
                }
            }

            // Process documents
            if (docFiles.Length > 0)
            {
                Console.WriteLine("\n========================================");
                Console.WriteLine("PROCESSING DOCUMENTS");
                Console.WriteLine("========================================");
                await reportWriter.WriteLineAsync("\n=== DOCUMENTS ===");

                for (int i = 0; i < docFiles.Length; i++)
                {
                    var docPath = docFiles[i];
                    var name = Path.GetFileName(docPath);
                    var size = new FileInfo(docPath).Length;

                    Console.WriteLine($"\n[{i + 1}/{docFiles.Length}] Processing: {name}");

                    if (_debugMode)
                    {
                        Console.WriteLine($"  • File size: {size / 1024.0:F1} KB");
                        Console.WriteLine($"  • Extension: {Path.GetExtension(docPath)}");
                    }

                    // File validation
                    if (size == 0)
                    {
                        docSkip++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⚠ SKIPPED: Empty file (0 bytes)");
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"⚠ SKIP: {name} - Empty file");
                        continue;
                    }

                    var ext = Path.GetExtension(docPath).ToLowerInvariant();
                    if (!IsValidDocumentType(ext))
                    {
                        docSkip++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⚠ SKIPPED: Unsupported type {ext}");
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"⚠ SKIP: {name} - Unsupported type");
                        continue;
                    }

                    // Check for HTML error pages disguised as documents
                    if (IsDocumentExtension(ext))
                    {
                        if (_debugMode)
                            Console.WriteLine($"  • Validating file integrity...");

                        var isValid = await ValidateFileAsync(docPath);
                        if (!isValid)
                        {
                            docQuarantined++;
                            var quarantinePath = Path.Combine(quarantineDir, name);
                            File.Move(docPath, quarantinePath, overwrite: true);

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  🔒 QUARANTINED: Invalid file → moved to _quarantine");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"🔒 QUARANTINE: {name} - Failed validation");
                            continue;
                        }
                    }

                    // Valid document - import via pipeline
                    try
                    {
                        if (_debugMode)
                            Console.WriteLine($"  • Importing via GovernanceImportPipeline...");

                        var progress = new Progress<ImportProgress>(p =>
                        {
                            if (!string.IsNullOrEmpty(p.Stage) && p.Stage != "Done")
                            {
                                if (_debugMode || p.Stage == "Error")
                                {
                                    Console.WriteLine($"    - {p.Stage}: {p.Info ?? ""}");
                                }
                            }
                        });

                        await _pipeline.ImportAsync(docPath, progress, i + 1, docFiles.Length, ct);

                        docOk++;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ SUCCESS: {name} imported");
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"✓ {name} ({size / 1024.0:F1} KB)");
                    }
                    catch (Exception ex)
                    {
                        docErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ ERROR: {ex.Message}");
                        if (_debugMode)
                        {
                            Console.WriteLine($"    Stack trace: {ex.StackTrace}");
                        }
                        Console.ResetColor();
                        await reportWriter.WriteLineAsync($"✗ {name}: {ex.Message}");
                    }
                }
            }

            // Summary
            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("IMPORT SUMMARY");
            Console.WriteLine(new string('=', 50));

            await reportWriter.WriteLineAsync($"\n=== SUMMARY ===");

            if (htmlFiles.Length > 0)
            {
                Console.WriteLine($"HTML Pages:");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Success: {htmlOk}");
                Console.ResetColor();

                if (htmlEmpty > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⚠ Empty/No chunks: {htmlEmpty}");
                    Console.ResetColor();
                }

                if (htmlErr > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Errors: {htmlErr}");
                    Console.ResetColor();
                }

                await reportWriter.WriteLineAsync($"HTML: {htmlOk} OK, {htmlEmpty} empty, {htmlErr} errors");
            }

            if (docFiles.Length > 0)
            {
                Console.WriteLine($"\nDocuments:");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Success: {docOk}");
                Console.ResetColor();

                if (docSkip > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⚠ Skipped: {docSkip}");
                    Console.ResetColor();
                }

                if (docQuarantined > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  🔒 Quarantined: {docQuarantined}");
                    Console.ResetColor();
                }

                if (docErr > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Errors: {docErr}");
                    Console.ResetColor();
                }

                await reportWriter.WriteLineAsync($"Docs: {docOk} OK, {docSkip} skipped, {docQuarantined} quarantined, {docErr} errors");
            }

            await reportWriter.WriteLineAsync($"\nReport saved to: {validationReportPath}");
            Console.WriteLine($"\n📋 Validation report: {validationReportPath}");

            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n[DEBUG] Import complete - check validation report for details");
                Console.ResetColor();
            }
        }

        // Add missing helper methods
        private static bool IsValidDocumentType(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            if (!ext.StartsWith(".")) ext = "." + ext;

            var validExtensions = new[] {
                ".pdf", ".doc", ".docx", ".xls", ".xlsx",
                ".ppt", ".pptx", ".txt", ".csv", ".rtf", ".zip"
            };

            return validExtensions.Contains(ext.ToLowerInvariant());
        }

        private static async Task<bool> ValidateFileAsync(string filePath)
        {
            try
            {
                // Simple validation - check if file can be opened and has valid magic bytes
                var bytes = await ReadFirstBytesAsync(filePath, 512);

                // Check for HTML error pages
                if (IsLikelyHtmlError(bytes))
                    return false;

                // Check magic bytes for known file types
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                return FileValidator.IsValid(bytes, ext);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Quarantine a file with detailed logging
        /// </summary>
        private void QuarantineFile(string filePath, string quarantineDir, string reason)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                var quarantinePath = Path.Combine(quarantineDir, fileName);

                // Create quarantine info file
                var infoPath = Path.Combine(quarantineDir, fileName + ".quarantine.txt");
                File.WriteAllText(infoPath, $"Original: {filePath}\nQuarantined: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nReason: {reason}");

                // Move the file
                File.Move(filePath, quarantinePath, overwrite: true);

                Console.WriteLine($"  🔒 Quarantined: {fileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ Failed to quarantine file: {ex.Message}");
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

                var info = string.IsNullOrWhiteSpace(p.Info) ? "" : $" - {p.Info}";
                // Using correct property names
                Console.WriteLine($"[{_name}] {p.Stage}{info}");
            }
        }
    }
}