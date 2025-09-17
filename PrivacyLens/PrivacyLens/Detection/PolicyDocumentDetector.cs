using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class PolicyDocumentDetector : BaseDocumentDetector
    {
        private readonly PolicyDetectionConfig _config;
        private readonly Regex _headerRegex;
        private readonly Regex _bulletRegex;

        public override string DetectorName => "Policy Document Detector";
        public override int Priority => 3;

        public PolicyDocumentDetector(ILogger<PolicyDocumentDetector> logger, IOptions<DetectionConfiguration> config)
            : base(logger)
        {
            _config = config.Value.Policy;
            _headerRegex = new Regex(_config.HeaderPattern, RegexOptions.Compiled | RegexOptions.Multiline);
            _bulletRegex = new Regex(_config.BulletPointPattern, RegexOptions.Compiled | RegexOptions.Multiline);
        }

        public override DetectionResult Detect(string content, string fileName = null)
        {
            var scanContent = GetScanContent(content);

            // Check for policy markers (like "Purpose:", "Legal Authority:")
            var policyMarkerCount = _config.PolicyMarkers
                .Count(marker => scanContent.Contains(marker, StringComparison.OrdinalIgnoreCase));

            // Count structured headers
            var headerMatches = _headerRegex.Matches(scanContent).Count;
            var bulletPoints = _bulletRegex.Matches(scanContent).Count;

            // Check for specific POPA-style patterns
            var hasPurpose = scanContent.Contains("Purpose:", StringComparison.OrdinalIgnoreCase);
            var hasLegalAuthority = scanContent.Contains("Legal Authority:", StringComparison.OrdinalIgnoreCase);
            var hasConsiderations = scanContent.Contains("Considerations:", StringComparison.OrdinalIgnoreCase);

            // Calculate confidence
            var confidence = 0.0;
            var reasons = new List<string>();

            // Policy markers are strong indicators
            if (policyMarkerCount >= 3)
            {
                confidence += 0.5;
                reasons.Add($"Found {policyMarkerCount} policy markers");
            }
            else if (policyMarkerCount >= _config.MinHeaderMatches)
            {
                confidence += 0.35;
                reasons.Add($"Found {policyMarkerCount} policy markers");
            }
            else if (policyMarkerCount > 0)
            {
                confidence += 0.2;
                reasons.Add($"Found {policyMarkerCount} policy markers");
            }

            // POPA-specific patterns
            if (hasPurpose && hasLegalAuthority)
            {
                confidence += 0.3;
                reasons.Add("POPA-style structure detected");
            }

            // Structured headers
            if (headerMatches >= 3)
            {
                confidence += 0.15;
                reasons.Add($"Found {headerMatches} structured headers");
            }

            // Bullet points indicate structured content
            if (bulletPoints >= 5)
            {
                confidence += 0.1;
                reasons.Add($"Found {bulletPoints} bullet points");
            }

            confidence = Math.Min(confidence, 1.0);

            // Check if we hit the threshold
            if (confidence >= 0.95)
            {
                LogDetection("Policy document detected", confidence, string.Join("; ", reasons));
                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = confidence,
                    DocumentType = "Policy",
                    RecommendedStrategy = "PolicyDocumentChunking",
                    Reasoning = string.Join("; ", reasons),
                    Metadata = new Dictionary<string, object>
                    {
                        ["PolicyMarkerCount"] = policyMarkerCount,
                        ["HeaderCount"] = headerMatches,
                        ["BulletPointCount"] = bulletPoints,
                        ["IsPOPAStyle"] = hasPurpose && hasLegalAuthority
                    }
                };
            }

            return DetectionResult.NoMatch;
        }
    }
}
