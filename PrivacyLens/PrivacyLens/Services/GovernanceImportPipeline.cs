using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using PrivacyLens.Models;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;
// Extraction libs
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Result returned from the classification pipeline
    /// </summary>
    public class ClassificationResult
    {
        public bool Success { get; set; }
        public string FileName { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public int ExtractedCharacters { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
    }

    /// <summary>
    /// Simplified import pipeline for testing classification
    /// TODO: Add chunking, embedding, and storage after classification is working
    /// </summary>
    public sealed class GovernanceImportPipeline
    {
        private readonly DocumentScoringEngine _scoringEngine;
        private readonly ILogger<GovernanceImportPipeline>? _logger;
        private readonly IConfiguration _config;
        private readonly bool _verbose;

        // Future: These will be needed when we add full pipeline
        // private readonly IChunkingService _chunking;
        // private readonly IEmbeddingService _embeddings;
        // private readonly IVectorStore _store;

        public GovernanceImportPipeline(
            IChunkingService chunking,
            IEmbeddingService embeddings,
            IVectorStore store,
            IConfiguration configuration,
            ILogger<GovernanceImportPipeline>? logger = null)
        {
            // Store these for future use when we implement full pipeline
            // _chunking = chunking ?? throw new ArgumentNullException(nameof(chunking));
            // _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
            // _store = store ?? throw new ArgumentNullException(nameof(store));

            _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger;

            // Load configuration
            var root = configuration.GetSection("AzureOpenAI");
            _verbose = root.GetValue<bool>("Diagnostics:Verbose", false);

            // Initialize the scoring engine for document classification
            _scoringEngine = InitializeScoringEngine(configuration);
        }

        private DocumentScoringEngine InitializeScoringEngine(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole());

            var serviceProvider = services.BuildServiceProvider();
            var scoringLogger = serviceProvider.GetService<ILogger<DocumentScoringEngine>>();

            var detectionConfig = configuration.GetSection("DocumentDetection");
            var confidenceThreshold = detectionConfig.GetValue<float>("ConfidenceThreshold", 70f);

            return new DocumentScoringEngine(
                logger: scoringLogger,
                useParallelProcessing: true,
                confidenceThreshold: confidenceThreshold
            );
        }

        /// <summary>
        /// Classify a document (extraction + classification only for now)
        /// </summary>
        /// <param name="filePath">Path to the file to classify</param>
        /// <param name="appId">Optional app ID for app-specific documents (null for corporate/governance docs)</param>
        /// <returns>Classification result</returns>
        public async Task<ClassificationResult> ClassifyDocumentAsync(string filePath, string? appId = null)
        {
            if (!File.Exists(filePath))
            {
                return new ClassificationResult
                {
                    Success = false,
                    FileName = Path.GetFileName(filePath),
                    Error = "File not found"
                };
            }

            var fileName = Path.GetFileName(filePath);
            var sw = Stopwatch.StartNew();

            try
            {
                // Stage 1: Text extraction
                if (_verbose)
                {
                    Console.WriteLine($"[Pipeline] Extracting text from {fileName}...");
                }

                var (text, title) = ExtractTextFromFile(filePath);

                if (string.IsNullOrWhiteSpace(text))
                {
                    return new ClassificationResult
                    {
                        Success = false,
                        FileName = fileName,
                        Error = "No text could be extracted from the document"
                    };
                }

                if (_verbose)
                {
                    Console.WriteLine($"[Pipeline] Extracted {text.Length:N0} characters");
                }

                // Stage 2: Document Classification
                if (_verbose)
                {
                    Console.WriteLine($"[Pipeline] Classifying document...");
                }

                var metadata = new DocumentMetadata
                {
                    FileName = fileName,
                    Title = title ?? Path.GetFileNameWithoutExtension(fileName)
                };

                var classification = await _scoringEngine.ClassifyDocumentAsync(text, metadata);

                string documentType = "Unknown";
                float confidence = 0;
                var evidence = new List<string>();

                if (classification.Success && classification.Confidence >= 50)
                {
                    documentType = classification.DocumentType;
                    confidence = classification.Confidence;

                    // Collect top evidence
                    if (classification.Evidence != null && classification.Evidence.Any())
                    {
                        evidence = classification.Evidence
                            .OrderByDescending(e => e.FinalScore)
                            .Take(3)
                            .Select(e => e.Feature)
                            .ToList();
                    }

                    if (_verbose)
                    {
                        Console.WriteLine($"[Pipeline] Classified as {documentType} with {confidence:F0}% confidence");
                        if (evidence.Any())
                        {
                            Console.WriteLine($"[Pipeline] Evidence: {string.Join(", ", evidence)}");
                        }
                    }
                }
                else
                {
                    if (_verbose)
                    {
                        Console.WriteLine($"[Pipeline] No clear classification (confidence: {classification?.Confidence ?? 0:F0}%)");
                    }
                }

                // Log app context if provided
                if (!string.IsNullOrEmpty(appId) && _verbose)
                {
                    Console.WriteLine($"[Pipeline] Document belongs to app: {appId}");
                }

                var result = new ClassificationResult
                {
                    Success = true,
                    FileName = fileName,
                    DocumentType = documentType,
                    Confidence = confidence,
                    ExtractedCharacters = text.Length,
                    Evidence = evidence,
                    Message = $"Classified as {documentType} ({confidence:F0}% confidence)"
                };

                if (_verbose)
                {
                    Console.WriteLine($"[Pipeline] Classification completed in {sw.Elapsed.TotalSeconds:F1}s");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Classification failed for {FileName}", fileName);

                return new ClassificationResult
                {
                    Success = false,
                    FileName = fileName,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Future: Full import with chunking, embedding, and storage
        /// </summary>
        /// <param name="filePath">Path to the file to import</param>
        /// <param name="appId">Optional app ID for app-specific documents</param>
        /// <returns>Import result</returns>
        public async Task<ClassificationResult> ImportAsync(string filePath, string? appId = null)
        {
            // For now, just do classification
            // TODO: Add chunking, embedding, and storage after classification is working
            var result = await ClassifyDocumentAsync(filePath, appId);

            if (result.Success && _verbose)
            {
                Console.WriteLine($"[Pipeline] TODO: Would chunk, embed, and store {result.FileName} as {result.DocumentType}");
                if (!string.IsNullOrEmpty(appId))
                {
                    Console.WriteLine($"[Pipeline] TODO: Would tag chunks with app_id: {appId}");
                }
            }

            return result;
        }

        private (string text, string? title) ExtractTextFromFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                return ext switch
                {
                    ".pdf" => ExtractPdf(filePath),
                    ".docx" => ExtractDocx(filePath),
                    ".doc" => ExtractLegacyDoc(filePath),
                    ".xlsx" => ExtractExcel(filePath),
                    ".xls" => ExtractLegacyExcel(filePath),
                    ".pptx" => ExtractPowerPoint(filePath),
                    ".ppt" => ExtractLegacyPowerPoint(filePath),
                    ".txt" => ExtractText(filePath),
                    ".md" => ExtractText(filePath),
                    ".html" => ExtractHtml(filePath),
                    ".htm" => ExtractHtml(filePath),
                    _ => throw new NotSupportedException($"File type {ext} is not supported")
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to extract text from {FilePath}", filePath);
                throw;
            }
        }

        private (string text, string? title) ExtractPdf(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = PdfDocument.Open(filePath))
            {
                if (doc.Information != null)
                {
                    title = doc.Information.Title;
                }

                foreach (var page in doc.GetPages())
                {
                    var pageText = page.Text;
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        sb.AppendLine(pageText);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(filePath);
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractDocx(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = WordprocessingDocument.Open(filePath, false))
            {
                var docProps = doc.PackageProperties;
                if (docProps != null)
                {
                    title = docProps.Title;
                }

                var body = doc.MainDocumentPart?.Document?.Body;
                if (body != null)
                {
                    foreach (var para in body.Elements<Paragraph>())
                    {
                        var text = para.InnerText;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            sb.AppendLine(text);
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(filePath);
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractLegacyDoc(string filePath)
        {
            // Legacy .doc files - placeholder for now
            // TODO: Implement using NPOI or similar library
            return ($"[Legacy Word document - content extraction not yet implemented]",
                    Path.GetFileNameWithoutExtension(filePath));
        }

        private (string text, string? title) ExtractExcel(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = SpreadsheetDocument.Open(filePath, false))
            {
                var docProps = doc.PackageProperties;
                if (docProps != null)
                {
                    title = docProps.Title;
                }

                var workbookPart = doc.WorkbookPart;
                if (workbookPart != null)
                {
                    foreach (var worksheetPart in workbookPart.WorksheetParts)
                    {
                        var sheetData = worksheetPart.Worksheet.Elements<SheetData>().FirstOrDefault();
                        if (sheetData != null)
                        {
                            foreach (var row in sheetData.Elements<Row>())
                            {
                                foreach (var cell in row.Elements<Cell>())
                                {
                                    var value = GetCellValue(cell, workbookPart);
                                    if (!string.IsNullOrWhiteSpace(value))
                                    {
                                        sb.Append(value + "\t");
                                    }
                                }
                                sb.AppendLine();
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(filePath);
            }

            return (sb.ToString(), title);
        }

        private string GetCellValue(Cell cell, WorkbookPart workbookPart)
        {
            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                var stringTable = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                if (stringTable != null)
                {
                    return stringTable.SharedStringTable.ElementAt(int.Parse(cell.InnerText)).InnerText;
                }
            }
            return cell.CellValue?.InnerText ?? string.Empty;
        }

        private (string text, string? title) ExtractLegacyExcel(string filePath)
        {
            // TODO: Implement using NPOI or similar library
            return ($"[Legacy Excel document - content extraction not yet implemented]",
                    Path.GetFileNameWithoutExtension(filePath));
        }

        private (string text, string? title) ExtractPowerPoint(string filePath)
        {
            var sb = new StringBuilder();
            string? title = null;

            using (var doc = PresentationDocument.Open(filePath, false))
            {
                var docProps = doc.PackageProperties;
                if (docProps != null)
                {
                    title = docProps.Title;
                }

                var presentationPart = doc.PresentationPart;
                if (presentationPart != null && presentationPart.Presentation != null)
                {
                    var presentation = presentationPart.Presentation;

                    if (presentation.SlideIdList != null)
                    {
                        foreach (var slideId in presentation.SlideIdList.Elements<SlideId>())
                        {
                            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId);

                            foreach (var text in slidePart.Slide.Descendants<A.Text>())
                            {
                                if (!string.IsNullOrWhiteSpace(text.Text))
                                {
                                    sb.AppendLine(text.Text);
                                }
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(filePath);
            }

            return (sb.ToString(), title);
        }

        private (string text, string? title) ExtractLegacyPowerPoint(string filePath)
        {
            // TODO: Implement using NPOI or similar library
            return ($"[Legacy PowerPoint document - content extraction not yet implemented]",
                    Path.GetFileNameWithoutExtension(filePath));
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

            // Simple HTML text extraction
            var text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ");

            return (text.Trim(), title);
        }
    }
}