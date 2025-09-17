using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class DocumentDetectionOrchestrator
    {
        private readonly List<IDocumentDetector> _detectors;
        private readonly ILogger<DocumentDetectionOrchestrator> _logger;
        private readonly double _confidenceThreshold;

        public DocumentDetectionOrchestrator(
            IEnumerable<IDocumentDetector> detectors,
            ILogger<DocumentDetectionOrchestrator> logger,
            IOptions<DetectionConfiguration> config)
        {
            _detectors = detectors.OrderBy(d => d.Priority).ToList();
            _logger = logger;
            _confidenceThreshold = config.Value.ConfidenceThreshold;
        }

        public async Task<(bool Success, string StrategyName, DetectionResult Result)> DetectAsync(
            string content,
            string fileName,
            IProgress<ProgressUpdate> progress = null)
        {
            _logger.LogInformation("Starting document detection for: {FileName}", fileName ?? "unnamed");

            progress?.Report(new ProgressUpdate
            {
                Phase = "Detection",
                Status = "Starting document type detection...",
                Icon = "🔍",
                Percentage = 5
            });

            var detectionResults = new List<DetectionResult>();

            foreach (var detector in _detectors)
            {
                progress?.Report(new ProgressUpdate
                {
                    Phase = "Detection",
                    Status = $"Checking: {detector.DetectorName}",
                    Icon = "🔍",
                    Percentage = 10 + (_detectors.IndexOf(detector) * 10)
                });

                try
                {
                    var result = detector.Detect(content, fileName);

                    if (result.CanHandle)
                    {
                        detectionResults.Add(result);
                        _logger.LogDebug("{Detector} returned confidence {Confidence:P}",
                            detector.DetectorName, result.Confidence);

                        // Early exit if we hit the confidence threshold
                        if (result.Confidence >= _confidenceThreshold)
                        {
                            _logger.LogInformation(
                                "Document detected as {Type} with {Confidence:P} confidence using {Detector}. Reason: {Reason}",
                                result.DocumentType, result.Confidence, detector.DetectorName, result.Reasoning);

                            progress?.Report(new ProgressUpdate
                            {
                                Phase = "Detection",
                                Status = $"✅ Detected: {result.DocumentType} ({result.Confidence:P0} confidence)",
                                Icon = "✅",
                                Detail = result.Reasoning,
                                Percentage = 100
                            });

                            return (true, result.RecommendedStrategy, result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in {Detector}", detector.DetectorName);
                }
            }

            // If we didn't hit the threshold but have results, take the highest confidence
            if (detectionResults.Any())
            {
                var best = detectionResults.OrderByDescending(r => r.Confidence).First();
                if (best.Confidence >= 0.7) // Lower threshold for "good enough"
                {
                    _logger.LogInformation(
                        "Using best match: {Type} with {Confidence:P} confidence. Reason: {Reason}",
                        best.DocumentType, best.Confidence, best.Reasoning);

                    progress?.Report(new ProgressUpdate
                    {
                        Phase = "Detection",
                        Status = $"⚠️ Detected: {best.DocumentType} ({best.Confidence:P0} confidence - below threshold)",
                        Icon = "⚠️",
                        Detail = best.Reasoning,
                        Percentage = 100
                    });

                    return (true, best.RecommendedStrategy, best);
                }
            }

            // No clear pattern detected
            _logger.LogInformation("No clear document pattern detected, will use AI analysis");

            progress?.Report(new ProgressUpdate
            {
                Phase = "Detection",
                Status = "❓ No clear pattern detected, will use AI analysis",
                Icon = "🤖",
                Percentage = 100
            });

            return (false, null, null);
        }
    }
}
