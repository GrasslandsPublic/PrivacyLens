using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Models;
using PrivacyLens.DocumentScoring.Detectors;
using PrivacyLens.DocumentScoring.FeatureExtraction;

namespace PrivacyLens.DocumentScoring.Core
{
    public class DocumentScoringEngine
    {
        private readonly List<IScoringDetector> _detectors;
        private readonly DocumentFeatureExtractor _featureExtractor;
        private readonly bool _useParallelProcessing;
        private readonly float _confidenceThreshold;
        private readonly ILogger<DocumentScoringEngine> _logger;

        public DocumentScoringEngine(
            ILogger<DocumentScoringEngine> logger = null,
            bool useParallelProcessing = true,
            float confidenceThreshold = 85f)
        {
            _logger = logger;
            _useParallelProcessing = useParallelProcessing;
            _confidenceThreshold = confidenceThreshold;
            _featureExtractor = new DocumentFeatureExtractor();
            _detectors = InitializeDetectors();
        }

        private List<IScoringDetector> InitializeDetectors()
        {
            var detectors = new List<IScoringDetector>
            {
                // Using the enhanced policy detector instead of the original
                new PolicyScoringDetectorEnhanced(_logger)
                // Add other detectors as they're implemented
                // Uncomment these as you implement them:
                // new TechnicalScoringDetector(_logger),
                // new FinancialScoringDetector(_logger),
                // new FormsScoringDetector(_logger),
                // new BoardScoringDetector(_logger),
                // new PrivacyScoringDetector(_logger),
                // new SecurityScoringDetector(_logger),
                // new WebScoringDetector(_logger)
            };

            return detectors.OrderBy(d => d.Priority).ToList();
        }

        public async Task<DocumentClassificationResult> ClassifyDocumentAsync(
            string content,
            DocumentMetadata metadata = null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new DocumentClassificationResult
                {
                    Success = false,
                    Error = "Document content is empty"
                };
            }

            var features = await Task.Run(() => _featureExtractor.ExtractFeatures(content, metadata));

            var scores = new List<DocumentConfidenceScore>();

            foreach (var detector in _detectors.Where(d => d.CanHandleDocument(metadata)))
            {
                var score = await Task.Run(() => detector.DetectWithScoring(content, features, metadata));
                scores.Add(score);
            }

            var bestScore = scores.OrderByDescending(s => s.NormalizedConfidence).FirstOrDefault();

            if (bestScore == null || bestScore.NormalizedConfidence < 30)
            {
                return new DocumentClassificationResult
                {
                    Success = false,
                    DocumentType = "Unknown",
                    Confidence = 0,
                    Method = "No match found"
                };
            }

            return new DocumentClassificationResult
            {
                Success = true,
                DocumentType = bestScore.DocumentType,
                Confidence = bestScore.NormalizedConfidence,
                ConfidenceLevel = bestScore.Level,
                Method = bestScore.NormalizedConfidence >= _confidenceThreshold ?
                    "Deterministic" : "Low Confidence",
                Evidence = bestScore.Evidence.OrderByDescending(e => e.FinalScore).Take(10).ToList(),
                Features = features
            };
        }
    }

    public class DocumentClassificationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public ConfidenceLevel ConfidenceLevel { get; set; }
        public string Method { get; set; }
        public bool RequiresAIVerification => Confidence < 85f;
        public List<ScoringEvidence> Evidence { get; set; }
        public DocumentFeatures Features { get; set; }
    }
}