// Services/CorporateScrapeImporter.cs - Enhanced with metadata extraction
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Chunking;
using PrivacyLens.Models;
using PrivacyLens.Services;
using SharpToken;
using HtmlAgilityPack;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enhanced importer with comprehensive metadata extraction
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

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Initialize tokenizer for statistics
            _tokenEncoder = GptEncoding.GetEncodingForModel("gpt-4");

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

            // HTML hybrid chunking components
            var boiler = new SimpleBoilerplateFilter();
            var seg = new HtmlDomSegmenter(boiler);
            var simple = new SimpleTextChunker();
            var gpt = new GptChunkingService(config, _logger);
            _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);

            // Embedding + Vector store
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
        }

        public async Task ImportScrapeAsync(string scrapeRoot, CancellationToken ct = default)
        {
            var pagesDir = Path.Combine(scrapeRoot, "webpages");
            var docsDir = Path.Combine(scrapeRoot, "documents");
            var quarantineDir = Path.Combine(scrapeRoot, "_quarantine");
            Directory.CreateDirectory(quarantineDir);

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

                        if (content.Length < 100)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠ SKIPPED: Too short ({content.Length} chars < 100)");
                            Console.ResetColor();
                            await reportWriter.WriteLineAsync($"SKIP: {name} - Too short ({content.Length} chars)");
                            htmlEmpty++;
                            continue;
                        }

                        // Extract metadata from HTML
                        var htmlMetadata = ExtractHtmlMetadata(content, htmlPath);

                        // Chunk HTML using hybrid strategy
                        if (_debugMode)
                            Console.WriteLine($"  • Chunking HTML content...");

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

                        Console.WriteLine($"   Generated {chunks.Count} chunks");
                        if (_debugMode)
                        {
                            Console.WriteLine($"   Title: {htmlMetadata.Title ?? "Not found"}");
                            Console.WriteLine($"   Type: {htmlMetadata.DocumentType}");
                        }

                        // Generate embeddings and enrich chunks with metadata
                        var enrichedChunks = new List<ChunkRecord>();
                        Console.Write($"   Generating embeddings and enriching metadata");

                        for (int j = 0; j < chunks.Count; j++)
                        {
                            if (j % 5 == 0) Console.Write(".");

                            var chunk = chunks[j];
                            var tokenCount = _tokenEncoder.Encode(chunk.Content).Count;
                            var embedding = await _embed.EmbedAsync(chunk.Content, ct);

                            // Create enriched chunk with metadata
                            var enrichedChunk = new ChunkRecord(
                                Index: chunk.Index,
                                Content: chunk.Content,
                                DocumentPath: chunk.DocumentPath,
                                Embedding: embedding
                            )
                            {
                                DocumentTitle = htmlMetadata.Title,
                                DocumentType = htmlMetadata.DocumentType,
                                SourceUrl = htmlMetadata.SourceUrl,  // Now contains actual URL from meta.json
                                DocumentHash = htmlMetadata.Hash,
                                TokenCount = tokenCount,
                                ChunkingStrategy = "HybridHTML",
                                DocumentCategory = ClassifyDocument(chunk.Content, htmlMetadata.Title),
                                DocumentDate = htmlMetadata.PublishDate ?? htmlMetadata.ScrapedAt,
                                ConfidenceScore = htmlMetadata.ValueScore,
                                Metadata = new Dictionary<string, object>
                                {
                                    ["filename"] = name,
                                    ["filesize"] = fileSize,
                                    ["import_date"] = DateTime.UtcNow.ToString("O"),
                                    ["scraped_at"] = htmlMetadata.ScrapedAt?.ToString("O") ?? "",
                                    ["word_count"] = htmlMetadata.WordCount ?? 0,
                                    ["is_valuable"] = htmlMetadata.IsValuable ?? false,
                                    ["description"] = htmlMetadata.Description ?? "",
                                    ["author"] = htmlMetadata.Author ?? ""
                                }
                            };

                            enrichedChunks.Add(enrichedChunk);

                            if (j < chunks.Count - 1)
                                await Task.Delay(50, ct);
                        }
                        Console.WriteLine(" done");

                        // Save to vector store
                        await _store.SaveChunksAsync(enrichedChunks, ct);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ SUCCESS: {name} → {chunks.Count} chunks saved with metadata");
                        Console.ResetColor();

                        await reportWriter.WriteLineAsync($"OK: {name} - {chunks.Count} chunks, Title: {htmlMetadata.Title}");
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

                        // Import using pipeline - it should handle documents properly
                        Console.Write($"  • Processing document with pipeline");

                        var progress = new Progress<ImportProgress>(p =>
                        {
                            if (p.Stage == "Chunk" && p.Info?.Contains("chunks=") == true)
                            {
                                Console.Write($" → {p.Info}");
                            }
                        });

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

            // Check vector store index
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

        private class HtmlMetadata
        {
            public string? Title { get; set; }
            public string DocumentType { get; set; } = "html";
            public string? SourceUrl { get; set; }
            public string? Hash { get; set; }
            public string? Description { get; set; }
            public string? Author { get; set; }
            public DateTime? PublishDate { get; set; }
            public DateTime? ScrapedAt { get; set; }
            public int? WordCount { get; set; }
            public double? ValueScore { get; set; }
            public bool? IsValuable { get; set; }
        }

        private HtmlMetadata ExtractHtmlMetadata(string html, string filePath)
        {
            var metadata = new HtmlMetadata
            {
                DocumentType = "html",
                SourceUrl = filePath // Default to filepath, will be overridden by meta.json
            };

            try
            {
                // First, try to load the corresponding meta.json file
                var metaJsonPath = Path.ChangeExtension(filePath, ".meta.json");
                if (File.Exists(metaJsonPath))
                {
                    try
                    {
                        var metaJson = File.ReadAllText(metaJsonPath);
                        var metaData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(metaJson);

                        if (metaData != null)
                        {
                            // Extract URL (most important)
                            if (metaData.TryGetValue("Url", out var url))
                            {
                                metadata.SourceUrl = url.ToString();
                            }

                            // Extract Title from meta.json
                            if (metaData.TryGetValue("Title", out var title))
                            {
                                metadata.Title = title.ToString();
                            }

                            // Extract ScrapedAt
                            if (metaData.TryGetValue("ScrapedAt", out var scrapedAt))
                            {
                                if (DateTime.TryParse(scrapedAt.ToString(), out var date))
                                {
                                    metadata.ScrapedAt = date;
                                }
                            }

                            // Extract WordCount
                            if (metaData.TryGetValue("WordCount", out var wordCount))
                            {
                                if (int.TryParse(wordCount.ToString(), out var count))
                                {
                                    metadata.WordCount = count;
                                }
                            }

                            // Extract ValueScore
                            if (metaData.TryGetValue("ValueScore", out var valueScore))
                            {
                                if (double.TryParse(valueScore.ToString(), out var score))
                                {
                                    metadata.ValueScore = score;
                                }
                            }

                            // Extract IsValuable
                            if (metaData.TryGetValue("IsValuable", out var isValuable))
                            {
                                if (bool.TryParse(isValuable.ToString(), out var valuable))
                                {
                                    metadata.IsValuable = valuable;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Warning: Could not parse meta.json: {ex.Message}");
                    }
                }

                // Parse HTML for additional metadata (as fallback or supplement)
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Extract title from HTML if not already set from meta.json
                if (string.IsNullOrWhiteSpace(metadata.Title))
                {
                    var titleNode = doc.DocumentNode.SelectSingleNode("//title");
                    if (titleNode != null)
                    {
                        metadata.Title = HtmlEntity.DeEntitize(titleNode.InnerText).Trim();
                    }

                    // Try meta og:title as fallback
                    if (string.IsNullOrWhiteSpace(metadata.Title))
                    {
                        var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
                        if (ogTitle != null)
                        {
                            metadata.Title = ogTitle.GetAttributeValue("content", "");
                        }
                    }
                }

                // Extract description
                var descNode = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
                if (descNode != null)
                {
                    metadata.Description = descNode.GetAttributeValue("content", "");
                }

                // Extract author
                var authorNode = doc.DocumentNode.SelectSingleNode("//meta[@name='author']");
                if (authorNode != null)
                {
                    metadata.Author = authorNode.GetAttributeValue("content", "");
                }

                // Try to extract publish date from HTML
                var dateNode = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']")
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@name='publish_date']");
                if (dateNode != null)
                {
                    var dateStr = dateNode.GetAttributeValue("content", "");
                    if (DateTime.TryParse(dateStr, out var date))
                    {
                        metadata.PublishDate = date;
                    }
                }

                // Generate content hash
                using (var sha256 = SHA256.Create())
                {
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(html));
                    metadata.Hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }

                // If no title found, use filename
                if (string.IsNullOrWhiteSpace(metadata.Title))
                {
                    metadata.Title = Path.GetFileNameWithoutExtension(filePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Error extracting HTML metadata: {ex.Message}");
                metadata.Title = Path.GetFileNameWithoutExtension(filePath);
            }

            return metadata;
        }

        private string? ClassifyDocument(string content, string? title = null)
        {
            // Use both content and title for better classification
            var combinedText = ((title ?? "") + " " + content).ToLower();

            // Priority-based classification (order matters)

            // Board and governance documents
            if (combinedText.Contains("board") && (combinedText.Contains("meeting") || combinedText.Contains("minutes") ||
                combinedText.Contains("highlights") || combinedText.Contains("trustees")))
            {
                return "Governance";
            }

            // Policy documents
            if (combinedText.Contains("policy") || combinedText.Contains("procedure") ||
                combinedText.Contains("regulation") || combinedText.Contains("compliance") ||
                combinedText.Contains("guidelines") || combinedText.Contains("standards"))
            {
                return "PolicyLegal";
            }

            // Educational/School content
            if (combinedText.Contains("student") || combinedText.Contains("school") ||
                combinedText.Contains("education") || combinedText.Contains("curriculum") ||
                combinedText.Contains("learning") || combinedText.Contains("teaching"))
            {
                return "Educational";
            }

            // Reports and analysis
            if (combinedText.Contains("report") || combinedText.Contains("analysis") ||
                combinedText.Contains("review") || combinedText.Contains("assessment") ||
                combinedText.Contains("evaluation") || combinedText.Contains("findings"))
            {
                return "Report";
            }

            // Communications and announcements
            if (combinedText.Contains("announcement") || combinedText.Contains("news") ||
                combinedText.Contains("update") || combinedText.Contains("newsletter") ||
                combinedText.Contains("bulletin") || combinedText.Contains("notice"))
            {
                return "Communications";
            }

            // Technical documentation
            if (combinedText.Contains("technical") || combinedText.Contains("engineering") ||
                combinedText.Contains("specification") || combinedText.Contains("implementation") ||
                combinedText.Contains("architecture") || combinedText.Contains("system"))
            {
                return "Technical";
            }

            // Forms and applications
            if (combinedText.Contains("form") || combinedText.Contains("application") ||
                combinedText.Contains("registration") || combinedText.Contains("template"))
            {
                return "Form";
            }

            // Administrative
            if (combinedText.Contains("administrative") || combinedText.Contains("administration") ||
                combinedText.Contains("office") || combinedText.Contains("staff"))
            {
                return "Administrative";
            }

            // Default for web content
            return "WebContent";
        }
    }
}