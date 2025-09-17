using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class LegalDocumentDetector : BaseDocumentDetector
    {
        private readonly LegalDetectionConfig _config;
        private readonly Regex _sectionRegex;
        private readonly Regex _numberingRegex;
        private readonly Regex _subsectionRegex;

        public override string DetectorName => "Legal Document Detector";
        public override int Priority => 2;

        public LegalDocumentDetector(ILogger<LegalDocumentDetector> logger, IOptions<DetectionConfiguration> config)
            : base(logger)
        {
            _config = config.Value.Legal;
            _sectionRegex = new Regex(_config.SectionPattern, RegexOptions.Compiled | RegexOptions.Multiline);
            _numberingRegex = new Regex(_config.LegalNumberingPattern, RegexOptions.Compiled | RegexOptions.Multiline);
            _subsectionRegex = new Regex(_config.SubsectionPattern, RegexOptions.Compiled | RegexOptions.Multiline);
        }

        public override DetectionResult Detect(string content, string fileName = null)
        {
            var scanContent = GetScanContent(content);

            // Check for legal markers
            var legalMarkerCount = _config.LegalMarkers
                .Count(marker => scanContent.Contains(marker, StringComparison.OrdinalIgnoreCase));

            // Count pattern matches
            var sectionMatches = _sectionRegex.Matches(scanContent).Count;
            var numberingMatches = _numberingRegex.Matches(scanContent).Count;
            var subsectionMatches = _subsectionRegex.Matches(scanContent).Count;

            // Calculate confidence
            var confidence = 0.0;
            var reasons = new List<string>();

            // Legal markers (strong indicator)
            if (legalMarkerCount >= 2)
            {
                confidence += 0.35;
                reasons.Add($"Found {legalMarkerCount} legal markers");
            }
            else if (legalMarkerCount == 1)
            {
                confidence += 0.15;
                reasons.Add("Found 1 legal marker");
            }

            // Section patterns
            if (sectionMatches >= _config.MinSectionMatches)
            {
                confidence += 0.35;
                reasons.Add($"Found {sectionMatches} section patterns");
            }
            else if (sectionMatches > 0)
            {
                confidence += 0.15;
                reasons.Add($"Found {sectionMatches} section patterns (below threshold)");
            }

            // Legal numbering
            if (numberingMatches >= _config.MinNumberingMatches)
            {
                confidence += 0.2;
                reasons.Add($"Found {numberingMatches} legal numbering patterns");
            }

            // Subsections
            if (subsectionMatches >= 3)
            {
                confidence += 0.1;
                reasons.Add($"Found {subsectionMatches} subsection patterns");
            }

            confidence = Math.Min(confidence, 1.0);

            // Check if we hit the threshold
            if (confidence >= 0.95)
            {
                LogDetection("Legal document detected", confidence, string.Join("; ", reasons));
                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = confidence,
                    DocumentType = "Legal",
                    RecommendedStrategy = "LegalDocumentChunking",
                    Reasoning = string.Join("; ", reasons),
                    Metadata = new Dictionary<string, object>
                    {
                        ["SectionCount"] = sectionMatches,
                        ["NumberingCount"] = numberingMatches,
                        ["SubsectionCount"] = subsectionMatches,
                        ["LegalMarkerCount"] = legalMarkerCount
                    }
                };
            }

            return DetectionResult.NoMatch;
        }
    }
}
