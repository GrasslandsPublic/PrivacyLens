using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class TechnicalDocumentDetector : BaseDocumentDetector
    {
        private readonly TechnicalDetectionConfig _config;
        private readonly Regex _codeBlockRegex;
        private readonly Regex _jsonRegex;

        public override string DetectorName => "Technical Document Detector";
        public override int Priority => 5;

        public TechnicalDocumentDetector(ILogger<TechnicalDocumentDetector> logger, IOptions<DetectionConfiguration> config)
            : base(logger)
        {
            _config = config.Value.Technical;
            _codeBlockRegex = new Regex(_config.CodeBlockPattern, RegexOptions.Compiled);
            _jsonRegex = new Regex(_config.JsonPattern, RegexOptions.Compiled);
        }

        public override DetectionResult Detect(string content, string fileName = null)
        {
            var scanContent = GetScanContent(content);

            // Check for API markers
            var apiMarkerCount = _config.ApiMarkers
                .Count(marker => scanContent.Contains(marker, StringComparison.OrdinalIgnoreCase));

            // Count technical patterns
            var codeBlocks = _codeBlockRegex.Matches(scanContent).Count;
            var jsonPatterns = _jsonRegex.Matches(scanContent).Count;

            // Look for specific technical patterns
            var hasEndpoints = scanContent.Contains("endpoint", StringComparison.OrdinalIgnoreCase) &&
                              (scanContent.Contains("GET", StringComparison.Ordinal) ||
                               scanContent.Contains("POST", StringComparison.Ordinal));

            var hasSchemas = scanContent.Contains("\"type\":", StringComparison.OrdinalIgnoreCase) &&
                            scanContent.Contains("\"properties\":", StringComparison.OrdinalIgnoreCase);

            // Calculate confidence
            var confidence = 0.0;
            var reasons = new List<string>();

            if (apiMarkerCount >= 4)
            {
                confidence += 0.4;
                reasons.Add($"Found {apiMarkerCount} API markers");
            }
            else if (apiMarkerCount >= 2)
            {
                confidence += 0.25;
                reasons.Add($"Found {apiMarkerCount} API markers");
            }

            if (codeBlocks >= _config.MinCodeBlocks)
            {
                confidence += 0.3;
                reasons.Add($"Found {codeBlocks} code blocks");
            }

            if (hasEndpoints)
            {
                confidence += 0.2;
                reasons.Add("API endpoints detected");
            }

            if (jsonPatterns >= 3)
            {
                confidence += 0.15;
                reasons.Add($"Found {jsonPatterns} JSON patterns");
            }

            if (hasSchemas)
            {
                confidence += 0.15;
                reasons.Add("JSON schemas detected");
            }

            confidence = Math.Min(confidence, 1.0);

            if (confidence >= 0.95)
            {
                LogDetection("Technical document detected", confidence, string.Join("; ", reasons));
                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = confidence,
                    DocumentType = "Technical",
                    RecommendedStrategy = "TechnicalDocumentChunking",
                    Reasoning = string.Join("; ", reasons),
                    Metadata = new Dictionary<string, object>
                    {
                        ["ApiMarkerCount"] = apiMarkerCount,
                        ["CodeBlockCount"] = codeBlocks,
                        ["JsonPatternCount"] = jsonPatterns,
                        ["HasEndpoints"] = hasEndpoints,
                        ["HasSchemas"] = hasSchemas
                    }
                };
            }

            return DetectionResult.NoMatch;
        }
    }
}
