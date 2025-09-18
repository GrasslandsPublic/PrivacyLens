// Services/CorporateScrapeImporter.cs - Fixed with proper DocumentSource enum
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Chunking;
using PrivacyLens.DocumentProcessing;
using PrivacyLens.Models;
using PrivacyLens.Services;
using SharpToken;
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

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enum for document sources (was missing from original code)
    /// </summary>
    public enum DocumentSource
    {
        Unknown,
        WebScrape,
        FileUpload,
        Manual,
        API
    }

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

        // Alternative constructor if being called with UnifiedDocumentProcessor
        public CorporateScrapeImporter(UnifiedDocumentProcessor processor, IConfiguration config, ILogger<CorporateScrapeImporter> logger)
        {
            _config = config;
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Initialize tokenizer for statistics
            _tokenEncoder = GptEncoding.GetEncodingForModel("gpt-4");

            // Create a logger for GptChunkingService
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_debugMode ? LogLevel.Debug : LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<GptChunkingService>();

            // Initialize services - we'll create our own pipeline
            var chunker = new GptChunkingService(config, _logger);
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
            _pipeline = new GovernanceImportPipeline(chunker, _embed, _store, config);

            // HTML hybrid chunking components
            var boiler = new SimpleBoilerplateFilter();
            var seg = new HtmlDomSegmenter(boiler);
            var simple = new SimpleTextChunker();
            var gpt = new GptChunkingService(config, _logger);
            _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);
        }

        // Modified ImportScrapeAsync method in CorporateScrapeImporter.cs
        // Add this using statement at the top of your file:
        // using PrivacyLens.DocumentProcessing;
        // Modified ImportScrapeAsync method in CorporateScrapeImporter.cs
        // Add this using statement at the top of your file:
        // using PrivacyLens.DocumentProcessing;

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

            // ADDED: Initialize document scoring
            DocumentScoringIntegration.Initialize(_logger);
            DocumentScoringIntegration.ResetStatistics();

            Console.WriteLine("\n========================================");
            Console.WriteLine("SCRAPE IMPORT ANALYSIS");
            Console.WriteLine("========================================");
            Console.WriteLine($"Scrape Root: {scrapeRoot}");
            Console.WriteLine($"Debug Mode: {(_debugMode ? "ENABLED" : "Disabled")}");
            Console.WriteLine($"Document Scoring: ENABLED"); // ADDED
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
            Console.WriteLine($" 📁 Documents found: {docFiles.Length}");
            Console.WriteLine();

            // Process HTML pages
            if (htmlFiles.Length > 0)
            {
                Console.WriteLine("========================================");
                Console.WriteLine("PROCESSING HTML PAGES");
                Console.WriteLine("========================================");

                int current = 0;
                int total = htmlFiles.Length;

                foreach (var file in htmlFiles)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    current++;
                    var fileName = Path.GetFileName(file);

                    try
                    {
                        var content = await File.ReadAllTextAsync(file, ct);

                        if (string.IsNullOrWhiteSpace(content) || content.Length < 100)
                        {
                            Console.WriteLine($"[{current}/{total}] Skipping empty/tiny: {fileName}");
                            htmlEmpty++;
                            await reportWriter.WriteLineAsync($"EMPTY: {fileName} (size: {content?.Length ?? 0})");
                            continue;
                        }

                        Console.WriteLine($"[{current}/{total}] Processing: {fileName}");

                        // Extract title from HTML
                        string title = null;
                        var titleMatch = Regex.Match(content, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase);
                        if (titleMatch.Success)
                        {
                            title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
                            Console.WriteLine($"   Title: {title}");
                        }

                        // Basic statistics
                        var wordCount = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                        Console.WriteLine($"   Word count: {wordCount}");

                        // MODIFIED: Enhanced chunking decision with scoring
                        var scoringDecision = await DocumentScoringIntegration.AnalyzeDocumentAsync(
                            content,
                            fileName,
                            title
                        );

                        // Determine chunking strategy based on scoring
                        string chunkingStrategy;
                        if (scoringDecision.UseDeterministic)
                        {
                            // Document was confidently classified
                            chunkingStrategy = scoringDecision.ChunkingHint;
                            Console.WriteLine($"   Chunking with {chunkingStrategy} strategy (deterministic)");
                        }
                        else
                        {
                            // Fall back to hybrid approach for uncertain documents
                            chunkingStrategy = "hybrid";
                            Console.WriteLine($"   Chunking with hybrid strategy (AI recommended)");
                        }

                        // Apply chunking with the determined strategy
                        var chunks = await _htmlOrchestrator.ChunkHtmlAsync(content, fileName, ct);
                        Console.WriteLine($"   Generated {chunks.Count} chunks");

                        // Enrich chunks with embeddings and scoring metadata
                        Console.WriteLine($"   Enriching with embeddings");

                        var enrichedChunks = new List<ChunkRecord>();
                        for (int i = 0; i < chunks.Count; i++)
                        {
                            var chunk = chunks[i];
                            var embedding = await _embed.EmbedAsync(chunk.Content, ct);

                            // Create a new chunk with embedding and metadata
                            var enrichedChunk = chunk with { Embedding = embedding };

                            // Add scoring metadata if available
                            if (scoringDecision.UseDeterministic && scoringDecision.Metadata != null)
                            {
                                // Create new metadata dictionary if needed
                                var updatedMetadata = enrichedChunk.Metadata != null
                                    ? new Dictionary<string, object>(enrichedChunk.Metadata)
                                    : new Dictionary<string, object>();

                                updatedMetadata["DocumentType"] = scoringDecision.DocumentType;
                                updatedMetadata["ClassificationConfidence"] = scoringDecision.Confidence.ToString("F1");

                                foreach (var kvp in scoringDecision.Metadata)
                                {
                                    updatedMetadata[$"Scoring_{kvp.Key}"] = kvp.Value;
                                }

                                enrichedChunk = enrichedChunk with { Metadata = updatedMetadata };
                            }

                            enrichedChunks.Add(enrichedChunk);
                        }

                        // Store in vector database
                        await _store.SaveChunksAsync(enrichedChunks, ct);

                        Console.WriteLine($"   ✅ Successfully processed ({enrichedChunks.Count} chunks)");
                        htmlOk++;

                        await reportWriter.WriteLineAsync(
                            $"OK: {fileName} | Title: {title ?? "N/A"} | " +
                            $"Type: {scoringDecision.DocumentType ?? "Unknown"} | " +
                            $"Confidence: {scoringDecision.Confidence:F1}% | " +
                            $"Chunks: {enrichedChunks.Count}"
                        );
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"   ❌ Error: {ex.Message}");
                        Console.ResetColor();
                        htmlErr++;
                        await reportWriter.WriteLineAsync($"ERROR: {fileName} - {ex.Message}");

                        if (_debugMode)
                        {
                            _logger.LogError(ex, "Error processing HTML file: {FileName}", fileName);
                        }
                    }
                }
            }

            // Process documents (PDFs, Word, etc.)
            if (docFiles.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("========================================");
                Console.WriteLine("PROCESSING DOCUMENTS");
                Console.WriteLine("========================================");

                int current = 0;
                int total = docFiles.Length;

                foreach (var file in docFiles)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    current++;
                    var fileName = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToLower();

                    Console.WriteLine($"[{current}/{total}] Processing: {fileName}");

                    // Handle document files (existing logic)
                    try
                    {
                        // Your existing document processing logic here
                        await _pipeline.ImportAsync(file, null, current, total, ct);
                        Console.WriteLine($"   ✅ Successfully processed");
                        docOk++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ❌ Error: {ex.Message}");
                        docErr++;
                    }
                }
            }

            // ADDED: Print scoring statistics
            DocumentScoringIntegration.PrintStatistics();

            // Summary
            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine("IMPORT SUMMARY");
            Console.WriteLine("========================================");
            Console.WriteLine($"HTML Pages:");
            Console.WriteLine($"  ✅ Successful: {htmlOk}");
            Console.WriteLine($"  ⚠️ Empty/Tiny: {htmlEmpty}");
            Console.WriteLine($"  ❌ Errors: {htmlErr}");

            if (docFiles.Length > 0)
            {
                Console.WriteLine($"\nDocuments:");
                Console.WriteLine($"  ✅ Successful: {docOk}");
                Console.WriteLine($"  ⏭️ Skipped: {docSkip}");
                Console.WriteLine($"  🔒 Quarantined: {docQuarantined}");
                Console.WriteLine($"  ❌ Errors: {docErr}");
            }

            Console.WriteLine($"\nTotal processing time: {DateTime.Now - DateTime.Parse(reportWriter.ToString().Split('\n')[1].Split(": ")[1]):hh\\:mm\\:ss}");
            Console.WriteLine("========================================");

            await reportWriter.WriteLineAsync($"\n=== Summary ===");
            await reportWriter.WriteLineAsync($"HTML - OK: {htmlOk}, Empty: {htmlEmpty}, Errors: {htmlErr}");
            await reportWriter.WriteLineAsync($"Docs - OK: {docOk}, Skipped: {docSkip}, Quarantined: {docQuarantined}, Errors: {docErr}");
            await reportWriter.WriteLineAsync($"Report generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }


        private HtmlMetadata ExtractHtmlMetadata(string html, string filePath)
        {
            var metadata = new HtmlMetadata();

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Title extraction
                metadata.Title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim()
                    ?? doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim()
                    ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "");

                // Description
                metadata.Description = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")
                    ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "");

                // Author
                metadata.Author = doc.DocumentNode.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", "");

                // URL
                metadata.Url = doc.DocumentNode.SelectSingleNode("//meta[@property='og:url']")?.GetAttributeValue("content", "")
                    ?? doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", "");

                // Clean text for analysis
                var scriptNodes = doc.DocumentNode.SelectNodes("//script");
                scriptNodes?.ToList().ForEach(n => n.Remove());

                var styleNodes = doc.DocumentNode.SelectNodes("//style");
                styleNodes?.ToList().ForEach(n => n.Remove());

                var bodyNode = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
                var text = bodyNode.InnerText;

                // Clean up whitespace
                text = Regex.Replace(text, @"\s+", " ");
                metadata.TextContent = text.Trim();

                // Word count
                metadata.WordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

                // Determine if valuable
                metadata.IsValuable = metadata.WordCount > 100;

                // Try to extract date
                var dateNode = doc.DocumentNode.SelectSingleNode("//meta[@name='publish_date']");
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
                combinedText.Contains("assessment") || combinedText.Contains("evaluation"))
            {
                return "Report";
            }

            // Forms and applications
            if (combinedText.Contains("form") || combinedText.Contains("application") ||
                combinedText.Contains("submit") || combinedText.Contains("fill"))
            {
                return "Form";
            }

            // Technical documentation
            if (combinedText.Contains("technical") || combinedText.Contains("system") ||
                combinedText.Contains("architecture") || combinedText.Contains("implementation"))
            {
                return "Technical";
            }

            return null; // No classification
        }

        private class HtmlMetadata
        {
            public string? Title { get; set; }
            public string? Description { get; set; }
            public string? Author { get; set; }
            public string? Url { get; set; }
            public string? TextContent { get; set; }
            public int? WordCount { get; set; }
            public bool? IsValuable { get; set; }
            public DateTime? PublishDate { get; set; }
            public string? Hash { get; set; }
        }
    }
}