using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentProcessing
{
    /// <summary>
    /// Integration layer for document scoring within the import pipeline
    /// Now with enhanced feedback for both positive and negative classifications
    /// </summary>
    public static class DocumentScoringIntegration
    {
        private static DocumentScoringEngine _scoringEngine;
        private static ILogger _logger;
        private static bool _isInitialized = false;

        // Statistics tracking
        private static int _totalDocuments = 0;
        private static int _deterministicCount = 0;
        private static int _aiRequiredCount = 0;
        private static int _rejectedAsNonPolicy = 0;
        private static int _navigationPages = 0;
        private static DateTime _sessionStart;

        /// <summary>
        /// Initialize the scoring integration
        /// </summary>
        public static void Initialize(ILogger logger = null)
        {
            if (!_isInitialized)
            {
                _logger = logger;

                // Create typed logger for DocumentScoringEngine if we have a logger
                if (logger != null)
                {
                    var loggerFactory = LoggerFactory.Create(builder => { });
                    var typedLogger = loggerFactory.CreateLogger<DocumentScoringEngine>();
                    _scoringEngine = new DocumentScoringEngine(typedLogger);
                }
                else
                {
                    _scoringEngine = new DocumentScoringEngine();
                }

                _sessionStart = DateTime.Now;
                _isInitialized = true;

                _logger?.LogInformation("Document Scoring Integration initialized");
            }
        }

        /// <summary>
        /// Reset statistics for a new import session
        /// </summary>
        public static void ResetStatistics()
        {
            _totalDocuments = 0;
            _deterministicCount = 0;
            _aiRequiredCount = 0;
            _rejectedAsNonPolicy = 0;
            _navigationPages = 0;
            _sessionStart = DateTime.Now;
        }

        /// <summary>
        /// Analyze a document and provide chunking recommendations
        /// </summary>
        public static async Task<ScoringDecision> AnalyzeDocumentAsync(
            string content,
            string fileName,
            string title = null)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            _totalDocuments++;

            try
            {
                // Check for navigation/index pages first
                if (IsNavigationPage(fileName, title, content))
                {
                    _navigationPages++;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"   [SKIPPED] Navigation/Index page detected");
                    Console.ResetColor();

                    return new ScoringDecision
                    {
                        UseDeterministic = false,
                        DocumentType = "Navigation/Index",
                        Confidence = 0,
                        ChunkingHint = "hybrid",
                        RequiresAI = false,
                        IsNavigationPage = true
                    };
                }

                // Create metadata for the document
                var metadata = new DocumentMetadata
                {
                    FileName = fileName,
                    FileType = System.IO.Path.GetExtension(fileName),
                    Source = "WebScrape"
                };

                // Add title to ExtractedFields if available
                if (!string.IsNullOrEmpty(title))
                {
                    metadata.ExtractedFields["Title"] = title;
                    metadata.Title = title;
                }

                // Perform classification
                var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

                // Handle high confidence classification
                if (result.Success && result.Confidence >= 85f)
                {
                    _deterministicCount++;

                    // High confidence classification
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"   [CLASSIFIED] {result.DocumentType} ({result.Confidence:F0}% confidence)");

                    // Show evidence if available
                    if (result.Evidence?.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"   Evidence: {result.Evidence[0].Feature}");
                    }
                    Console.ResetColor();

                    return new ScoringDecision
                    {
                        UseDeterministic = true,
                        DocumentType = result.DocumentType,
                        Confidence = result.Confidence,
                        ChunkingHint = GetChunkingStrategy(result.DocumentType),
                        Metadata = ExtractMetadata(result)
                    };
                }
                // Handle low confidence but detected type
                else if (result.Success && result.Confidence > 0)
                {
                    _aiRequiredCount++;

                    // Low confidence - would need AI
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"   [LOW CONFIDENCE] Possible {result.DocumentType} ({result.Confidence:F0}%)");
                    Console.ResetColor();

                    return new ScoringDecision
                    {
                        UseDeterministic = false,
                        DocumentType = result.DocumentType,
                        Confidence = result.Confidence,
                        ChunkingHint = "hybrid",
                        RequiresAI = true
                    };
                }
                // Handle explicit rejection (confidence = 0)
                else if (result.Confidence == 0)
                {
                    _rejectedAsNonPolicy++;

                    // Determine why it was rejected
                    string rejectionReason = DetermineRejectionReason(fileName, title, result);

                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"   [NOT A POLICY] {rejectionReason}");
                    Console.ResetColor();

                    _aiRequiredCount++;

                    return new ScoringDecision
                    {
                        UseDeterministic = false,
                        DocumentType = "General Content",
                        Confidence = 0,
                        ChunkingHint = "hybrid",
                        RequiresAI = true,
                        RejectionReason = rejectionReason
                    };
                }
                // No match found
                else
                {
                    _aiRequiredCount++;

                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"   [UNCLASSIFIED] No document type detected");
                    Console.ResetColor();

                    _logger?.LogDebug("No deterministic match for {FileName}", fileName);

                    return new ScoringDecision
                    {
                        UseDeterministic = false,
                        ChunkingHint = "hybrid",
                        RequiresAI = true
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during document analysis for {FileName}", fileName);
                _aiRequiredCount++;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   [ERROR] Classification failed: {ex.Message}");
                Console.ResetColor();

                return new ScoringDecision
                {
                    UseDeterministic = false,
                    ChunkingHint = "hybrid",
                    RequiresAI = true,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Check if this is a navigation or index page
        /// </summary>
        private static bool IsNavigationPage(string fileName, string title, string content)
        {
            // Check for "Documents |" pattern in title
            if (title?.StartsWith("Documents |", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            // Check for index/navigation patterns in filename
            if (fileName?.Contains("documents_", StringComparison.OrdinalIgnoreCase) == true &&
                !fileName.Contains("article", StringComparison.OrdinalIgnoreCase))
                return true;

            // Check for very low content after stripping HTML
            var textOnly = System.Text.RegularExpressions.Regex.Replace(content ?? "", @"<[^>]+>", " ");
            if (textOnly.Trim().Length < 200)
                return true;

            return false;
        }

        /// <summary>
        /// Determine why a document was rejected as a policy
        /// </summary>
        private static string DetermineRejectionReason(string fileName, string title, DocumentClassificationResult result)
        {
            // Check filename indicators
            if (fileName?.Contains("article_", StringComparison.OrdinalIgnoreCase) == true)
                return "Article/News content";

            if (fileName?.Contains("announcement", StringComparison.OrdinalIgnoreCase) == true)
                return "Announcement";

            if (fileName?.Contains("newsletter", StringComparison.OrdinalIgnoreCase) == true)
                return "Newsletter";

            // Check title indicators
            if (title != null)
            {
                if (title.Contains("Update", StringComparison.OrdinalIgnoreCase))
                    return "Update/Notice";

                if (title.Contains("Announcement", StringComparison.OrdinalIgnoreCase))
                    return "Announcement";

                if (title.Contains("Newsletter", StringComparison.OrdinalIgnoreCase))
                    return "Newsletter";

                if (title.Contains("Election", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Nomination", StringComparison.OrdinalIgnoreCase))
                    return "Election/Administrative notice";

                if (title.Contains("Back to School", StringComparison.OrdinalIgnoreCase))
                    return "Informational content";
            }

            // Check if there was evidence but still rejected
            if (result?.Evidence?.Count > 0)
                return $"Insufficient policy indicators (highest: {result.Evidence[0].Feature})";

            return "General content - no policy markers found";
        }

        /// <summary>
        /// Print statistics at the end of import session
        /// </summary>
        public static void PrintStatistics()
        {
            if (_totalDocuments == 0) return;

            var deterministicRate = (_deterministicCount * 100.0) / _totalDocuments;
            var aiRate = (_aiRequiredCount * 100.0) / _totalDocuments;
            var rejectionRate = (_rejectedAsNonPolicy * 100.0) / _totalDocuments;
            var navigationRate = (_navigationPages * 100.0) / _totalDocuments;
            var estimatedSavings = _deterministicCount * 0.001; // $0.001 per AI call saved

            Console.WriteLine();
            Console.WriteLine("╔════════════════════════════════════════╗");
            Console.WriteLine("║     DOCUMENT SCORING STATISTICS       ║");
            Console.WriteLine("╠════════════════════════════════════════╣");
            Console.WriteLine($"║ Total Processed:     {_totalDocuments,17} ║");
            Console.WriteLine($"║ High Confidence:     {_deterministicCount,17} ║");
            Console.WriteLine($"║ Low Confidence:      {_aiRequiredCount - _rejectedAsNonPolicy - _navigationPages,17} ║");
            Console.WriteLine($"║ Rejected (Not Policy): {_rejectedAsNonPolicy,14} ║");
            Console.WriteLine($"║ Navigation Pages:    {_navigationPages,17} ║");
            Console.WriteLine($"║ ────────────────────────────────────── ║");
            Console.WriteLine($"║ Deterministic Rate:      {deterministicRate,10:F1}% ║");
            Console.WriteLine($"║ AI Required Rate:        {aiRate,10:F1}% ║");
            Console.WriteLine($"║ Rejection Rate:          {rejectionRate,10:F1}% ║");
            Console.WriteLine($"║ Navigation Rate:         {navigationRate,10:F1}% ║");
            Console.WriteLine($"║ Estimated Savings:      ${estimatedSavings,10:F3} ║");
            Console.WriteLine("╚════════════════════════════════════════╝");

            _logger?.LogInformation(
                "Document Scoring Session Complete: {Total} documents, {Deterministic} deterministic ({Rate:F1}%), " +
                "{Rejected} rejected as non-policy, {Navigation} navigation pages, Savings: ${Savings:F3}",
                _totalDocuments, _deterministicCount, deterministicRate,
                _rejectedAsNonPolicy, _navigationPages, estimatedSavings);
        }

        /// <summary>
        /// Get chunking strategy name based on document type
        /// </summary>
        private static string GetChunkingStrategy(string documentType)
        {
            return documentType switch
            {
                "Policy & Legal" => "structure-preserve",
                "Technical" => "code-aware",
                "Financial" => "table-aware",
                "Forms & Templates" => "form-preserve",
                "Board Documents" => "chronological",
                "Meeting Minutes" => "semantic",
                "Educational" => "topic-based",
                _ => "hybrid"
            };
        }

        /// <summary>
        /// Extract metadata from classification result
        /// </summary>
        private static Dictionary<string, string> ExtractMetadata(DocumentClassificationResult result)
        {
            var metadata = new Dictionary<string, string>
            {
                ["DocumentType"] = result.DocumentType,
                ["Confidence"] = result.Confidence.ToString("F1"),
                ["Method"] = result.Method,
                ["ConfidenceLevel"] = result.ConfidenceLevel.ToString()
            };

            // Add evidence if available
            if (result.Evidence?.Count > 0)
            {
                metadata["PrimaryEvidence"] = result.Evidence[0].Feature;
                metadata["EvidenceScore"] = result.Evidence[0].FinalScore.ToString("F1");
            }

            return metadata;
        }
    }

    /// <summary>
    /// Decision result from scoring analysis
    /// </summary>
    public class ScoringDecision
    {
        public bool UseDeterministic { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public string ChunkingHint { get; set; }
        public bool RequiresAI { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public string Error { get; set; }
        public string RejectionReason { get; set; }
        public bool IsNavigationPage { get; set; }
    }
}