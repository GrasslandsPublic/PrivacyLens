// Modified GovernanceImportPipeline.cs with Detection Integration
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrivacyLens.Diagnostics;
using PrivacyLens.Models;
using PrivacyLens.DocumentProcessing.Detection;
using PrivacyLens.DocumentProcessing.Models;
// Extraction libs
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using System.ClientModel;
using SharpToken;
using SharpToken; // For GptEncoder if using SharpToken, otherwise remove this line

namespace PrivacyLens.Services
{
    public sealed class GovernanceImportPipeline
    {
        private readonly IChunkingService _chunking;
        private readonly IEmbeddingService _embeddings;
        private readonly IVectorStore _store;
        private readonly ILogger<GovernanceImportPipeline>? _logger;
        private readonly IConfiguration _config;
        private readonly bool _verbose;
        private readonly bool _showStageDurations;
        private readonly int _maxRetries;
        private readonly int _baseDelayMs;
        private readonly int _maxDelayMs;
        private readonly int _jitterMs;
        private readonly int _minDelayBetweenRequestsMs;
        private readonly int? _fixedEmbeddingDimension;
        // Remove GptEncoder since it's not being used in detection
        // private readonly GptEncoder? _enc;

        // Detection components
        private readonly DocumentDetectionOrchestrator? _detectionOrchestrator;
        private readonly bool _useDetection;

        public GovernanceImportPipeline(
            IChunkingService chunking,
            IEmbeddingService embeddings,
            IVectorStore store,
            IConfiguration configuration,
            ILogger<GovernanceImportPipeline>? logger = null)
        {
            _chunking = chunking ?? throw new ArgumentNullException(nameof(chunking));
            _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger;

            // Load configuration
            var root = configuration.GetSection("AzureOpenAI");
            _verbose = root.GetValue<bool>("Diagnostics:Verbose", false);
            _showStageDurations = root.GetValue<bool>("Diagnostics:ShowStageDurations", false);

            var retry = root.GetSection("Retry");
            _maxRetries = retry.GetValue<int>("MaxRetries", 3);
            _baseDelayMs = retry.GetValue<int>("BaseDelayMs", 1500);
            _maxDelayMs = retry.GetValue<int>("MaxDelayMs", 60000);
            _jitterMs = retry.GetValue<int>("JitterMs", 250);
            _minDelayBetweenRequestsMs = retry.GetValue<int>("MinDelayBetweenRequestsMs", 750);

            _fixedEmbeddingDimension = configuration.GetValue<int?>("VectorStore:DesiredEmbeddingDim");

            // Removed GptEncoder initialization since it's not needed for detection

            // Initialize detection - Check for both "Enabled" and default to true if section exists
            var detectionSection = configuration.GetSection("DocumentDetection");
            _useDetection = detectionSection.Exists() && detectionSection.GetValue<bool>("Enabled", true);

            if (_useDetection)
            {
                try
                {
                    _detectionOrchestrator = InitializeDetection(configuration);
                    _logger?.LogInformation("Document detection initialized successfully");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✅ Document detection is ENABLED");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not initialize detection. Falling back to standard chunking.");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠️ Detection initialization failed: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"   Inner error: {ex.InnerException.Message}");
                    }
                    Console.WriteLine("   Falling back to standard GPT chunking");
                    Console.ResetColor();
                    _useDetection = false;
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("⚠️ Document detection is DISABLED (add 'DocumentDetection:Enabled': true to appsettings.json)");
                Console.ResetColor();
            }
        }

        private DocumentDetectionOrchestrator InitializeDetection(IConfiguration configuration)
        {
            // Create a mini DI container for detection
            var services = new ServiceCollection();

            // Register configuration
            services.Configure<DetectionConfiguration>(configuration.GetSection("DocumentDetection"));

            // Register loggers
            services.AddLogging(builder => builder.AddConsole());

            // Register detectors
            services.AddScoped<IDocumentDetector, HtmlDocumentDetector>();
            services.AddScoped<IDocumentDetector, LegalDocumentDetector>();
            services.AddScoped<IDocumentDetector, MarkdownDocumentDetector>();
            services.AddScoped<IDocumentDetector, PolicyDocumentDetector>();
            services.AddScoped<IDocumentDetector, TechnicalDocumentDetector>();

            // Build provider
            var serviceProvider = services.BuildServiceProvider();

            // Create orchestrator
            var detectors = serviceProvider.GetServices<IDocumentDetector>();
            var orchestratorLogger = serviceProvider.GetService<ILogger<DocumentDetectionOrchestrator>>();
            var config = serviceProvider.GetService<IOptions<DetectionConfiguration>>();

            return new DocumentDetectionOrchestrator(detectors, orchestratorLogger, config);
        }

        public async Task ImportAsync(
            string filePath,
            IProgress<ImportProgress>? progress = null,
            int current = 0,  // Changed from fileNumber to current for compatibility
            int total = 0,    // Changed from totalFiles to total for compatibility
            CancellationToken ct = default)
        {
            var fileName = Path.GetFileName(filePath);
            var fileInfo = "";
            if (total > 0)
                fileInfo = $"[{current}/{total}] ";

            try
            {
                // Stage 1: Extract text
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Extract",
                    $"{fileInfo}Reading {fileName}"));

                var (text, title) = await ExtractTextAsync(filePath, ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("No text content extracted from document");
                }

                // Stage 2: Detection (NEW!)
                if (_useDetection && _detectionOrchestrator != null)
                {
                    await PerformDetectionWithVisualization(text, fileName, progress, current, total);
                }

                // Stage 3: Chunk
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Chunk",
                    $"{fileInfo}Creating semantic chunks"));

                var chunks = await ChunkWithRetryAsync(text, filePath, ct);
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Chunk",
                    $"{fileInfo}Created {chunks.Count} chunks"));

                // Stage 4: Embed
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Embed",
                    $"{fileInfo}Generating embeddings"));

                await EmbedChunksWithRetryAsync(chunks, progress, current, total, fileName, ct);
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Embed",
                    $"{fileInfo}Embeddings complete"));

                // Stage 5: Store
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Store",
                    $"{fileInfo}Saving to database"));

                // Use the chunks with embeddings if we have them, otherwise use original chunks
                var chunksToStore = _chunksWithEmbeddings ?? chunks.ToList();
                await _store.SaveChunksAsync(chunksToStore, ct);
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Done",
                    $"{fileInfo}Successfully imported {chunksToStore.Count} chunks"));

                // Clear the temporary field
                _chunksWithEmbeddings = null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to import {FileName}", fileName);
                progress?.Report(new ImportProgress(
                    current, total, fileName, "Error",
                    $"{fileInfo}Failed: {ex.Message}"));
                throw;
            }
        }

        private async Task PerformDetectionWithVisualization(
            string text,
            string fileName,
            IProgress<ImportProgress>? progress,
            int current,
            int total)
        {
            try
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ══════════════════════════════════════════════════════");
                Console.WriteLine($"  📋 DOCUMENT DETECTION ANALYSIS");
                Console.WriteLine($"  ══════════════════════════════════════════════════════");
                Console.ResetColor();

                // Create a progress reporter for detection
                var detectionProgress = new Progress<PrivacyLens.DocumentProcessing.Models.ProgressUpdate>(update =>
                {
                    // Display detection progress
                    if (update.Icon == "🔍")
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"    {update.Icon} Checking: {update.Status?.Replace("Checking: ", "")}");
                        Console.ResetColor();
                    }
                    else if (update.Icon == "✅")
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"    {update.Icon} {update.Status}");
                        if (!string.IsNullOrEmpty(update.Detail))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"       Reasoning: {update.Detail}");
                        }
                        Console.ResetColor();
                    }
                    else if (update.Icon == "⚠")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"    {update.Icon} {update.Status}");
                        if (!string.IsNullOrEmpty(update.Detail))
                        {
                            Console.WriteLine($"       Note: {update.Detail}");
                        }
                        Console.ResetColor();
                    }
                });

                progress?.Report(new ImportProgress(
                    current, total, fileName, "Detect",
                    "Analyzing document structure"));

                // Run detection
                var detectionResult = await _detectionOrchestrator.DetectAsync(
                    text,
                    fileName,
                    detectionProgress);

                var success = detectionResult.Success;
                var strategyName = detectionResult.StrategyName;
                var result = detectionResult.Result;

                // Display results
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ──────────────────────────────────────────────────────");
                Console.WriteLine($"  📊 DETECTION RESULTS:");
                Console.WriteLine($"  ──────────────────────────────────────────────────────");
                Console.ResetColor();

                if (success)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"    ✅ Document Type: {result.DocumentType}");
                    Console.WriteLine($"    📈 Confidence: {result.Confidence:P0}");
                    Console.WriteLine($"    🎯 Strategy: {strategyName}");
                    Console.ResetColor();

                    // Display metadata if present
                    if (result.Metadata?.Any() == true)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"    📝 Metadata:");
                        foreach (var kvp in result.Metadata.Take(5)) // Show first 5 metadata items
                        {
                            Console.WriteLine($"       • {kvp.Key}: {kvp.Value}");
                        }
                        Console.ResetColor();
                    }

                    progress?.Report(new ImportProgress(
                        current, total, fileName, "Detect",
                        $"Detected as {result.DocumentType} ({result.Confidence:P0})"));
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    ⚠ No confident detection - will use standard chunking");
                    if (result != null)
                    {
                        Console.WriteLine($"    📈 Best match: {result.DocumentType} ({result.Confidence:P0})");
                    }
                    Console.ResetColor();

                    progress?.Report(new ImportProgress(
                        current, total, fileName, "Detect",
                        "No confident detection, using standard approach"));
                }

                // Show what will happen next
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"  💡 Next Step: {(success ? $"Apply {strategyName} chunking strategy" : "Use standard GPT chunking")}");
                Console.ResetColor();
                Console.WriteLine($"  ══════════════════════════════════════════════════════");
                Console.WriteLine();

                // Small delay to let user see the results
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Detection failed, continuing with standard chunking");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    ❌ Detection error: {ex.Message}");
                Console.ResetColor();

                progress?.Report(new ImportProgress(
                    current, total, fileName, "Detect",
                    "Detection failed, using standard approach"));
            }
        }

        // Rest of the implementation remains the same as original...
        private async Task<(string text, string? title)> ExtractTextAsync(string filePath, CancellationToken ct)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => ExtractPdf(filePath),
                ".doc" or ".docx" => ExtractWord(filePath),
                ".xls" or ".xlsx" => ExtractExcel(filePath),
                ".ppt" or ".pptx" => ExtractPowerPoint(filePath),
                ".txt" or ".md" or ".json" or ".xml" or ".csv" => ExtractText(filePath),
                ".html" or ".htm" => ExtractHtml(filePath),
                _ => throw new NotSupportedException($"File type {ext} is not supported")
            };
        }

        private (string text, string? title) ExtractPdf(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = PdfDocument.Open(filePath))
            {
                title = doc.Information?.Title;
                if (string.IsNullOrWhiteSpace(title))
                    title = Path.GetFileNameWithoutExtension(filePath);

                foreach (var page in doc.GetPages())
                {
                    sb.AppendLine(page.Text);
                }
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractWord(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = WordprocessingDocument.Open(filePath, false))
            {
                title = doc.PackageProperties?.Title;
                if (string.IsNullOrWhiteSpace(title))
                    title = Path.GetFileNameWithoutExtension(filePath);

                var body = doc.MainDocumentPart?.Document?.Body;
                if (body != null)
                {
                    foreach (var para in body.Elements<Paragraph>())
                    {
                        sb.AppendLine(para.InnerText);
                    }
                }
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractExcel(string filePath)
        {
            var sb = new StringBuilder();
            string? title = Path.GetFileNameWithoutExtension(filePath);

            using (var doc = SpreadsheetDocument.Open(filePath, false))
            {
                var workbook = doc.WorkbookPart;
                if (workbook != null)
                {
                    var sheets = workbook.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>();
                    foreach (var sheet in sheets)
                    {
                        sb.AppendLine($"Sheet: {sheet.Name}");
                        // Note: Full Excel extraction would require more complex cell reading
                        // This is simplified for demonstration
                    }
                }
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractPowerPoint(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = PresentationDocument.Open(filePath, false))
            {
                title = doc.PackageProperties?.Title;
                if (string.IsNullOrWhiteSpace(title))
                    title = Path.GetFileNameWithoutExtension(filePath);

                var presentation = doc.PresentationPart?.Presentation;
                if (presentation != null)
                {
                    var slideIds = presentation.SlideIdList?.Elements<SlideId>();
                    if (slideIds != null)
                    {
                        foreach (var slideId in slideIds)
                        {
                            var slide = (SlidePart)doc.PresentationPart.GetPartById(slideId.RelationshipId);
                            var texts = slide.Slide.Descendants<A.Text>();
                            foreach (var text in texts)
                            {
                                sb.AppendLine(text.Text);
                            }
                        }
                    }
                }
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractText(string filePath)
        {
            var text = File.ReadAllText(filePath);
            var title = Path.GetFileNameWithoutExtension(filePath);
            return (text, title);
        }

        private (string text, string? title) ExtractHtml(string filePath)
        {
            var html = File.ReadAllText(filePath);
            var title = Path.GetFileNameWithoutExtension(filePath);

            // Simple HTML text extraction (could be enhanced with HtmlAgilityPack)
            var text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ");

            return (text.Trim(), title);
        }

        private async Task<IReadOnlyList<ChunkRecord>> ChunkWithRetryAsync(
            string text,
            string filePath,
            CancellationToken ct)
        {
            return await ExecuteWithRetryAsync(
                async () => await _chunking.ChunkAsync(text, filePath, null, ct),
                "Chunking",
                ct);
        }

        private async Task EmbedChunksWithRetryAsync(
            IReadOnlyList<ChunkRecord> chunks,
            IProgress<ImportProgress>? progress,
            int current,
            int total,
            string fileName,
            CancellationToken ct)
        {
            // Since chunks is readonly and ChunkRecord properties are init-only,
            // we need to create new chunks with embeddings
            var chunksWithEmbeddings = new List<ChunkRecord>();
            var fileInfo = total > 0 ? $"[{current}/{total}] " : "";

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (i > 0)
                {
                    await Task.Delay(_minDelayBetweenRequestsMs, ct);
                }

                progress?.Report(new ImportProgress(
                    current, total, fileName, "Embed",
                    $"{fileInfo}Embedding chunk {i + 1}/{chunks.Count}"));

                var embedding = await ExecuteWithRetryAsync(
                    async () => await _embeddings.EmbedAsync(chunk.Text, ct),
                    $"Embedding chunk {i + 1}",
                    ct);

                // Resize if needed
                if (_fixedEmbeddingDimension.HasValue &&
                    embedding.Length != _fixedEmbeddingDimension.Value)
                {
                    embedding = ResizeEmbedding(embedding, _fixedEmbeddingDimension.Value);
                }

                // Create a new chunk using the constructor with embedding
                var newChunk = new ChunkRecord(
                    chunk.Index,
                    chunk.Content,
                    chunk.DocumentPath,
                    embedding,
                    chunk.DocumentTitle,
                    chunk.DocumentType,
                    chunk.SourceUrl,
                    chunk.TokenCount
                )
                {
                    // Set additional properties using init syntax
                    DocumentCategory = chunk.DocumentCategory,
                    DocumentHash = chunk.DocumentHash,
                    DocStructure = chunk.DocStructure,
                    Sensitivity = chunk.Sensitivity,
                    ChunkingStrategy = chunk.ChunkingStrategy,
                    SourceSection = chunk.SourceSection,
                    PageNumber = chunk.PageNumber,
                    Jurisdiction = chunk.Jurisdiction,
                    RegulationRefs = chunk.RegulationRefs,
                    RiskLevel = chunk.RiskLevel,
                    RequiresReview = chunk.RequiresReview,
                    DataElements = chunk.DataElements,
                    ThirdParties = chunk.ThirdParties,
                    RetentionPeriod = chunk.RetentionPeriod,
                    DocumentDate = chunk.DocumentDate,
                    ConfidenceScore = chunk.ConfidenceScore,
                    OverlapPrevious = chunk.OverlapPrevious,
                    OverlapNext = chunk.OverlapNext,
                    Metadata = chunk.Metadata
                };

                chunksWithEmbeddings.Add(newChunk);
            }

            // Store the chunks with embeddings for later use
            _chunksWithEmbeddings = chunksWithEmbeddings;
        }

        // Temporary field to hold chunks with embeddings
        private List<ChunkRecord>? _chunksWithEmbeddings;

        private async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            string operationName,
            CancellationToken ct)
        {
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    var delay = CalculateDelay(attempt);
                    _logger?.LogWarning(ex,
                        "{Operation} failed on attempt {Attempt}. Retrying in {Delay}ms",
                        operationName, attempt + 1, delay);
                    await Task.Delay(delay, ct);
                }
            }

            // This will throw the last exception
            return await operation();
        }

        private int CalculateDelay(int attempt)
        {
            var exponentialDelay = _baseDelayMs * Math.Pow(2, attempt);
            var delay = Math.Min(exponentialDelay, _maxDelayMs);
            var jitter = Random.Shared.Next(-_jitterMs, _jitterMs);
            return (int)delay + jitter;
        }

        private float[] ResizeEmbedding(float[] embedding, int targetSize)
        {
            if (embedding.Length == targetSize)
                return embedding;

            var resized = new float[targetSize];
            if (embedding.Length > targetSize)
            {
                Array.Copy(embedding, resized, targetSize);
            }
            else
            {
                Array.Copy(embedding, resized, embedding.Length);
            }
            return resized;
        }

        // Add the missing ImportTextAsync method for compatibility
        public async Task ImportTextAsync(
            string text,
            string sourceName,
            IProgress<ImportProgress>? progress = null,
            int current = 1,  // Add these parameters
            int total = 1,    // Add these parameters
            CancellationToken ct = default)
        {
            try
            {
                // Stage 1: Detection
                if (_useDetection && _detectionOrchestrator != null)
                {
                    await PerformDetectionWithVisualization(text, sourceName, progress, current, total);
                }

                // Stage 2: Chunk
                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Chunk",
                    $"Creating semantic chunks for {sourceName}"));

                var chunks = await ChunkWithRetryAsync(text, sourceName, ct);
                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Chunk",
                    $"Created {chunks.Count} chunks"));

                // Stage 3: Embed
                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Embed",
                    "Generating embeddings"));

                await EmbedChunksWithRetryAsync(chunks, progress, current, total, sourceName, ct);

                // Stage 4: Store
                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Store",
                    "Saving to database"));

                var chunksToStore = _chunksWithEmbeddings ?? chunks.ToList();
                await _store.SaveChunksAsync(chunksToStore, ct);

                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Done",
                    $"Successfully imported {chunksToStore.Count} chunks from {sourceName}"));

                _chunksWithEmbeddings = null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to import text from {SourceName}", sourceName);
                progress?.Report(new ImportProgress(
                    current, total, sourceName, "Error",
                    $"Failed: {ex.Message}"));
                throw;
            }
        }
    }

    // Removed the duplicate ImportProgress class - using the one from PrivacyLens.Models namespace
}