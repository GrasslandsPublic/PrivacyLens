using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class MarkdownDocumentDetector : BaseDocumentDetector
    {
        private readonly MarkdownDetectionConfig _config;
        private readonly Regex _headerRegex;
        private static readonly Regex CodeBlockRegex = new(@"```[\s\S]*?```", RegexOptions.Compiled);
        private static readonly Regex ListItemRegex = new(@"^\s*[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        public override string DetectorName => "Markdown Document Detector";
        public override int Priority => 4;

        public MarkdownDocumentDetector(ILogger<MarkdownDocumentDetector> logger, IOptions<DetectionConfiguration> config)
            : base(logger)
        {
            _config = config.Value.Markdown;
            _headerRegex = new Regex(_config.HeaderPattern, RegexOptions.Compiled | RegexOptions.Multiline);
        }

        public override DetectionResult Detect(string content, string fileName = null)
        {
            // Check file extension
            if (!string.IsNullOrEmpty(fileName) && HasFileExtension(fileName, ".md", ".markdown"))
            {
                // Strong indicator
                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = 0.98,
                    DocumentType = "Markdown",
                    RecommendedStrategy = "MarkdownChunking",
                    Reasoning = "Markdown file extension",
                    Metadata = new Dictionary<string, object>()
                };
            }

            var scanContent = GetScanContent(content);

            // Count markdown elements
            var headerCount = _headerRegex.Matches(scanContent).Count;
            var codeBlocks = CodeBlockRegex.Matches(scanContent).Count;
            var listItems = ListItemRegex.Matches(scanContent).Count;
            var links = LinkRegex.Matches(scanContent).Count;

            // Calculate confidence
            var confidence = 0.0;
            var reasons = new List<string>();

            if (headerCount >= _config.MinHeaderCount)
            {
                confidence += 0.4;
                reasons.Add($"Found {headerCount} markdown headers");
            }
            else if (headerCount > 0)
            {
                confidence += 0.2;
                reasons.Add($"Found {headerCount} markdown headers");
            }

            if (codeBlocks >= 2)
            {
                confidence += 0.3;
                reasons.Add($"Found {codeBlocks} code blocks");
            }
            else if (codeBlocks > 0)
            {
                confidence += 0.15;
                reasons.Add($"Found {codeBlocks} code block");
            }

            if (listItems >= _config.MinListItems)
            {
                confidence += 0.2;
                reasons.Add($"Found {listItems} list items");
            }

            if (links >= 3)
            {
                confidence += 0.1;
                reasons.Add($"Found {links} markdown links");
            }

            confidence = Math.Min(confidence, 1.0);

            if (confidence >= 0.95)
            {
                LogDetection("Markdown detected", confidence, string.Join("; ", reasons));
                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = confidence,
                    DocumentType = "Markdown",
                    RecommendedStrategy = "MarkdownChunking",
                    Reasoning = string.Join("; ", reasons),
                    Metadata = new Dictionary<string, object>
                    {
                        ["HeaderCount"] = headerCount,
                        ["CodeBlockCount"] = codeBlocks,
                        ["ListItemCount"] = listItems,
                        ["LinkCount"] = links
                    }
                };
            }

            return DetectionResult.NoMatch;
        }
    }
}