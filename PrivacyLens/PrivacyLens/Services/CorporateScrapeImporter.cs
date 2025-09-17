// Services/CorporateScrapeImporter.cs - Enhanced with embedding fix and detailed reporting
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Chunking;
using PrivacyLens.Models;
using PrivacyLens.Services;
using SharpToken;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enhanced importer for scrape folders with comprehensive file validation and statistics:
    ///   • HTML in /webpages → DOM-segment + hybrid chunking → embed + save
    ///   • Docs in /documents → validate → pipeline.ImportAsync
    ///   • Invalid files → quarantine with detailed logging
    ///   • Enhanced reporting with token statistics
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
        private readonly GptEncoding _tokenEncoder;

        // Chunking statistics tracking
        private class ChunkingStats
        {
            public int TotalChunks { get; set; }
            public int MinTokens { get; set; } = int.MaxValue;
            public int MaxTokens { get; set; }
            public long TotalTokens { get; set; }
            public bool UsedPanelization { get; set; }
            public int PanelCount { get; set; }

            public double AverageTokens => TotalChunks > 0 ? (double)TotalTokens / TotalChunks : 0;

            public void UpdateWithChunk(int tokenCount)
            {
                TotalChunks++;
                TotalTokens += tokenCount;
                MinTokens = Math.Min(MinTokens, tokenCount);
                MaxTokens = Math.Max(MaxTokens, tokenCount);
            }

            public string GetSummary()
            {
                if (TotalChunks == 0) return "No chunks generated";

                var sb = new StringBuilder();
                sb.Append($"{TotalChunks} chunks");
                sb.Append($" | Tokens: min={MinTokens}, avg={AverageTokens:F0}, max={MaxTokens}");
                if (UsedPanelization)
                    sb.Append($" | Panelized: {PanelCount} panels");
                return sb.ToString();
            }
        }

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;

            // Check if debug mode is enabled
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Initialize tokenizer for statistics
            _tokenEncoder = GptEncoding.GetEncodingForModel("gpt-4");

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

                        var stats = new ChunkingStats();
                        var chunks = await _htmlOrchestrator.ChunkHtmlAsync(content, htmlPath, ct);

                        if (chunks == null || chunks.Count == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ WARNING: No chunks generated");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"WARN: {name} - No chunks generated");
                            htmlEmpty++;
                            continue;
                        }

                        // Calculate chunking statistics
                        foreach (var chunk in chunks)
                        {
                            var tokenCount = _tokenEncoder.Encode(chunk.Content).Count;
                            stats.UpdateWithChunk(tokenCount);
                        }

                        Console.WriteLine($"   Generated {chunks.Count} chunks");
                        if (_debugMode)
                        {
                            Console.WriteLine($"   {stats.GetSummary()}");
                        }

                        // FIX: Generate embeddings for each chunk (this was missing!)
                        var chunksWithEmbeddings = new List<ChunkRecord>();
                        Console.Write($"   Generating embeddings");

                        for (int j = 0; j < chunks.Count; j++)
                        {
                            if (j % 5 == 0) Console.Write(".");

                            var chunk = chunks[j];
                            var embedding = await _embed.EmbedAsync(chunk.Content, ct);

                            chunksWithEmbeddings.Add(new ChunkRecord(
                                chunk.Index,
                                chunk.Content,
                                chunk.DocumentPath,
                                embedding
                            ));

                            // Small delay to avoid rate limiting
                            if (j < chunks.Count - 1)
                                await Task.Delay(50, ct);
                        }
                        Console.WriteLine(" done");

                        // Save to vector store
                        await _store.SaveChunksAsync(chunksWithEmbeddings, ct);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ SUCCESS: {name} → {chunks.Count} chunks saved");
                        Console.ResetColor();

                        await reportWriter.WriteLineAsync($"OK: {name} - {stats.GetSummary()}");
                        htmlOk++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ ERROR: {ex.Message}");
                        Console.ResetColor();

                        if (_debugMode)
                        {
                            Console.WriteLine($"  Stack: {ex.StackTrace}");
                        }

                        await reportWriter.WriteLineAsync($"ERROR: {name} - {ex.Message}");
                        htmlErr++;
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
                    var ext = Path.GetExtension(docPath).ToLowerInvariant();

                    Console.WriteLine($"\n[{i + 1}/{docFiles.Length}] Processing: {name}");

                    try
                    {
                        var fileInfo = new FileInfo(docPath);

                        // Basic validation
                        if (fileInfo.Length < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ QUARANTINE: Suspiciously small file ({fileInfo.Length} bytes)");
                            Console.ResetColor();

                            var quarantinePath = Path.Combine(quarantineDir, name);
                            File.Move(docPath, quarantinePath, overwrite: true);
                            await reportWriter.WriteLineAsync($"QUARANTINE: {name} - Too small ({fileInfo.Length} bytes)");
                            docQuarantined++;
                            continue;
                        }

                        // Check if it's a supported document type
                        var supportedExtensions = new[] { ".pdf", ".doc", ".docx", ".txt", ".csv", ".xlsx", ".xls" };
                        if (!supportedExtensions.Contains(ext))
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ SKIPPED: Unsupported file type ({ext})");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"SKIP: {name} - Unsupported type");
                            docSkip++;
                            continue;
                        }

                        // Use the pipeline to import the document
                        Console.Write($"  • Processing document with pipeline");

                        var progress = new Progress<ImportProgress>(p =>
                        {
                            if (p.Stage == "Chunk" && p.Info?.Contains("chunks=") == true)
                            {
                                Console.Write($" → {p.Info}");
                            }
                        });

                        // Import using the pipeline (this handles chunking + embedding)
                        await _pipeline.ImportAsync(
                            docPath,
                            progress: progress,
                            current: i + 1,
                            total: docFiles.Length,
                            ct: ct
                        );

                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ SUCCESS: {name} imported");
                        Console.ResetColor();

                        await reportWriter.WriteLineAsync($"OK: {name} - Imported via pipeline");
                        docOk++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n  ✗ ERROR: {ex.Message}");
                        Console.ResetColor();

                        if (_debugMode)
                        {
                            Console.WriteLine($"  Stack: {ex.StackTrace}");
                        }

                        await reportWriter.WriteLineAsync($"ERROR: {name} - {ex.Message}");
                        docErr++;
                    }
                }
            }

            // Summary
            Console.WriteLine("\n========================================");
            Console.WriteLine("IMPORT SUMMARY");
            Console.WriteLine("========================================");
            Console.WriteLine($"HTML Pages:");
            Console.WriteLine($"  ✓ Successful: {htmlOk}");
            Console.WriteLine($"  ⚠ Empty/Skipped: {htmlEmpty}");
            Console.WriteLine($"  ✗ Failed: {htmlErr}");
            Console.WriteLine($"\nDocuments:");
            Console.WriteLine($"  ✓ Successful: {docOk}");
            Console.WriteLine($"  ⚠ Skipped: {docSkip}");
            Console.WriteLine($"  🔒 Quarantined: {docQuarantined}");
            Console.WriteLine($"  ✗ Failed: {docErr}");
            Console.WriteLine();

            await reportWriter.WriteLineAsync($"\n=== SUMMARY ===");
            await reportWriter.WriteLineAsync($"HTML: OK={htmlOk}, Empty={htmlEmpty}, Error={htmlErr}");
            await reportWriter.WriteLineAsync($"Docs: OK={docOk}, Skip={docSkip}, Quarantine={docQuarantined}, Error={docErr}");
            await reportWriter.WriteLineAsync($"Report generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            Console.WriteLine($"📊 Validation report saved to: {validationReportPath}");

            // Create index if needed
            Console.WriteLine("\n🔍 Checking vector store index...");
            try
            {
                await _store.InitializeAsync(ct);
                Console.WriteLine("  ✓ Vector store index ready");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ Index creation warning: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}