// Services/CorporateScrapeImporter.cs - Complete Enhanced Version
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
using PrivacyLens.DocumentProcessing;
using SharpToken;
using HtmlAgilityPack;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enum for document sources
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
    /// Enhanced importer with comprehensive metadata extraction and document classification
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

            // Initialize document scoring
            DocumentScoringIntegration.Initialize(_logger);
            DocumentScoringIntegration.ResetStatistics();

            Console.WriteLine("\n========================================");
            Console.WriteLine("SCRAPE IMPORT ANALYSIS");
            Console.WriteLine("========================================");
            Console.WriteLine($"Scrape Root: {scrapeRoot}");
            Console.WriteLine($"Debug Mode: {(_debugMode ? "ENABLED" : "Disabled")}");
            Console.WriteLine($"Document Scoring: ENABLED");
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

                        // Enhanced chunking decision with scoring
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

                            // Create a new chunk with embedding
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

                    try
                    {
                        // Step 1: Extract text content for classification
                        string extractedText = null;
                        string documentTitle = null;

                        try
                        {
                            Console.WriteLine($"   Extracting text from {ext.ToUpper()}...");

                            switch (ext)
                            {
                                case ".pdf":
                                    extractedText = await ExtractPdfTextAsync(file);
                                    documentTitle = ExtractPdfTitle(file);
                                    break;

                                case ".docx":
                                case ".doc":
                                    extractedText = await ExtractWordTextAsync(file);
                                    documentTitle = ExtractWordTitle(file);
                                    break;

                                case ".txt":
                                    extractedText = await File.ReadAllTextAsync(file, ct);
                                    break;

                                default:
                                    // For other formats, use the pipeline's default extraction
                                    extractedText = null;
                                    break;
                            }

                            if (!string.IsNullOrWhiteSpace(extractedText))
                            {
                                var wordCount = extractedText.Split(new[] { ' ', '\n', '\r', '\t' },
                                    StringSplitOptions.RemoveEmptyEntries).Length;
                                Console.WriteLine($"   Word count: {wordCount}");

                                if (!string.IsNullOrWhiteSpace(documentTitle))
                                {
                                    Console.WriteLine($"   Title: {documentTitle}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"   ⚠️ Could not extract text for classification: {ex.Message}");
                            extractedText = null;
                        }

                        // Step 2: Apply document scoring if text was extracted
                        string chunkingStrategy = "default";
                        Dictionary<string, string> scoringMetadata = null;

                        if (!string.IsNullOrWhiteSpace(extractedText))
                        {
                            var scoringDecision = await DocumentScoringIntegration.AnalyzeDocumentAsync(
                                extractedText,
                                fileName,
                                documentTitle
                            );

                            if (scoringDecision.UseDeterministic)
                            {
                                // High confidence classification
                                chunkingStrategy = scoringDecision.ChunkingHint;
                                scoringMetadata = scoringDecision.Metadata;

                                Console.WriteLine($"   Strategy: {chunkingStrategy} chunking");
                            }
                            else if (scoringDecision.IsNavigationPage)
                            {
                                // Skip navigation/index documents
                                Console.WriteLine($"   ⏭️ Skipping navigation/index document");
                                docSkip++;
                                await reportWriter.WriteLineAsync($"SKIPPED: {fileName} - Navigation/Index page");
                                continue;
                            }
                            else if (scoringDecision.Confidence == 0)
                            {
                                // Document was actively rejected
                                Console.WriteLine($"   Strategy: Standard GPT chunking");
                            }
                            else
                            {
                                // Low confidence or unclassified
                                Console.WriteLine($"   Strategy: Standard GPT chunking");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"   Strategy: Standard extraction (no text preview available)");
                        }

                        // Step 3: Process document through pipeline
                        var progress = new Progress<ImportProgress>(p =>
                        {
                            if (!string.IsNullOrWhiteSpace(p.Stage) && p.Stage != "Done" && p.Stage != "Error")
                            {
                                Console.WriteLine($"   {p.Stage}: {p.Info ?? ""}");
                            }
                        });

                        // Process through the governance pipeline
                        await _pipeline.ImportAsync(file, progress, current, total, ct);

                        Console.WriteLine($"   ✅ Successfully processed");
                        docOk++;

                        // Log to report with classification info
                        if (scoringMetadata != null)
                        {
                            await reportWriter.WriteLineAsync(
                                $"OK: {fileName} | Type: {scoringMetadata.GetValueOrDefault("DocumentType", "Unknown")} | " +
                                $"Confidence: {scoringMetadata.GetValueOrDefault("Confidence", "N/A")}%"
                            );
                        }
                        else
                        {
                            await reportWriter.WriteLineAsync($"OK: {fileName} | Type: Unclassified");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ❌ Error: {ex.Message}");
                        docErr++;
                        await reportWriter.WriteLineAsync($"ERROR: {fileName} - {ex.Message}");

                        if (_debugMode)
                        {
                            _logger.LogError(ex, "Error processing document: {FileName}", fileName);
                        }
                    }
                }
            }

            // Print scoring statistics
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

            var startTime = await ReadFirstLineDate(validationReportPath);
            var totalTime = DateTime.Now - startTime;
            Console.WriteLine($"\nTotal processing time: {totalTime:hh\\:mm\\:ss}");
            Console.WriteLine("========================================");

            await reportWriter.WriteLineAsync($"\n=== Summary ===");
            await reportWriter.WriteLineAsync($"HTML - OK: {htmlOk}, Empty: {htmlEmpty}, Errors: {htmlErr}");
            await reportWriter.WriteLineAsync($"Docs - OK: {docOk}, Skipped: {docSkip}, Quarantined: {docQuarantined}, Errors: {docErr}");
            await reportWriter.WriteLineAsync($"Report generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        #region Document Text Extraction Methods

        private async Task<string> ExtractPdfTextAsync(string filePath)
        {
            try
            {
                using (var document = PdfDocument.Open(filePath))
                {
                    var text = new StringBuilder();

                    // Extract first few pages for classification (max 10 pages or 50KB of text)
                    int pagesToExtract = Math.Min(document.NumberOfPages, 10);

                    for (int i = 1; i <= pagesToExtract; i++)
                    {
                        var page = document.GetPage(i);
                        text.AppendLine(page.Text);

                        // Stop if we have enough text
                        if (text.Length > 50000) break;
                    }

                    return text.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract PDF text for classification: {File}", filePath);
                return null;
            }
        }

        private string ExtractPdfTitle(string filePath)
        {
            try
            {
                using (var document = PdfDocument.Open(filePath))
                {
                    // Try to get title from PDF metadata
                    if (!string.IsNullOrWhiteSpace(document.Information.Title))
                    {
                        return document.Information.Title;
                    }

                    // Otherwise, try to extract from first page
                    if (document.NumberOfPages > 0)
                    {
                        var firstPage = document.GetPage(1);
                        var lines = firstPage.Text.Split('\n')
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .Take(3)
                            .ToArray();

                        if (lines.Length > 0)
                        {
                            // Often the title is the first non-empty line
                            return lines[0].Trim();
                        }
                    }
                }
            }
            catch
            {
                // Fallback to filename without extension
            }

            return Path.GetFileNameWithoutExtension(filePath);
        }

        private async Task<string> ExtractWordTextAsync(string filePath)
        {
            try
            {
                using (var doc = WordprocessingDocument.Open(filePath, false))
                {
                    var text = new StringBuilder();
                    var body = doc.MainDocumentPart.Document.Body;

                    // Extract first portion of document for classification
                    int paragraphCount = 0;
                    foreach (var paragraph in body.Elements<Paragraph>())
                    {
                        text.AppendLine(paragraph.InnerText);
                        paragraphCount++;

                        // Limit extraction for classification purposes
                        if (paragraphCount > 50 || text.Length > 50000) break;
                    }

                    return text.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract Word text for classification: {File}", filePath);
                return null;
            }
        }

        private string ExtractWordTitle(string filePath)
        {
            try
            {
                using (var doc = WordprocessingDocument.Open(filePath, false))
                {
                    // Try to get title from document properties
                    var props = doc.PackageProperties;
                    if (!string.IsNullOrWhiteSpace(props.Title))
                    {
                        return props.Title;
                    }

                    // Otherwise, get first heading or paragraph
                    var body = doc.MainDocumentPart.Document.Body;
                    var firstText = body.Elements<Paragraph>()
                        .Select(p => p.InnerText)
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

                    if (!string.IsNullOrWhiteSpace(firstText))
                    {
                        return firstText.Length > 100 ? firstText.Substring(0, 100) + "..." : firstText;
                    }
                }
            }
            catch
            {
                // Fallback to filename
            }

            return Path.GetFileNameWithoutExtension(filePath);
        }

        private async Task<DateTime> ReadFirstLineDate(string filePath)
        {
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    await reader.ReadLineAsync(); // Skip header
                    var timeLine = await reader.ReadLineAsync();
                    if (timeLine != null && timeLine.StartsWith("Time: "))
                    {
                        var dateStr = timeLine.Substring(6);
                        return DateTime.Parse(dateStr);
                    }
                }
            }
            catch
            {
                // Fallback to current time
            }
            return DateTime.Now;
        }

        #endregion
    }
}