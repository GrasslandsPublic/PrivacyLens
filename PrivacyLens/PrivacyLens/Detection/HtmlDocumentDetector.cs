// File: Detection/HtmlDocumentDetector.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public class HtmlDocumentDetector : BaseDocumentDetector
    {
        private readonly HtmlDetectionConfig _config;
        private readonly Dictionary<string, Regex> _patterns;

        public override string DetectorName => "HTML Document Detector";
        public override int Priority => 1;

        public HtmlDocumentDetector(ILogger<HtmlDocumentDetector> logger, IOptions<DetectionConfiguration> config)
            : base(logger)
        {
            _config = config.Value.Html;
            _patterns = InitializePatterns();
        }

        private Dictionary<string, Regex> InitializePatterns()
        {
            var patterns = new Dictionary<string, Regex>();

            // Load patterns from configuration or use defaults
            if (_config.Patterns != null)
            {
                foreach (var pattern in _config.Patterns)
                {
                    try
                    {
                        patterns[pattern.Key] = new Regex(pattern.Value, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        _logger.LogDebug($"Loaded HTML pattern '{pattern.Key}': {pattern.Value}");
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning($"Invalid regex pattern for '{pattern.Key}': {ex.Message}. Using default.");
                        patterns[pattern.Key] = GetDefaultPattern(pattern.Key);
                    }
                }
            }

            // Ensure all required patterns exist (use defaults if missing)
            EnsureRequiredPatterns(patterns);

            return patterns;
        }

        private Regex GetDefaultPattern(string key)
        {
            return key switch
            {
                "HtmlTag" => new Regex(@"<\/?[a-z][\s\S]*?>", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "HeaderTag" => new Regex(@"<h[1-6][\s>]", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "DivWithClass" => new Regex(@"<div\s+class=", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "MainContent" => new Regex(@"<(main|article|section)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "MetaTag" => new Regex(@"<meta\s+[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "LinkTag" => new Regex(@"<link\s+[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "HeadSection" => new Regex(@"<head[\s>][\s\S]*?</head>", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "BodyTag" => new Regex(@"<body[\s>]", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "DocType" => new Regex(@"^\s*<!DOCTYPE", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                "HtmlOpenTag" => new Regex(@"^\s*<html", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                _ => new Regex(@".*", RegexOptions.Compiled)
            };
        }

        private void EnsureRequiredPatterns(Dictionary<string, Regex> patterns)
        {
            var requiredPatterns = new[] { "HtmlTag", "HeaderTag", "DivWithClass", "MainContent",
                                          "MetaTag", "LinkTag", "HeadSection", "BodyTag",
                                          "DocType", "HtmlOpenTag" };

            foreach (var key in requiredPatterns)
            {
                if (!patterns.ContainsKey(key))
                {
                    patterns[key] = GetDefaultPattern(key);
                    _logger.LogDebug($"Using default pattern for '{key}'");
                }
            }
        }

        public override DetectionResult Detect(string content, string fileName = null)
        {
            // Quick file extension check using configurable extensions
            if (!string.IsNullOrEmpty(fileName))
            {
                var fileExtensions = _config.FileExtensions ?? new[] { ".html", ".htm", ".xhtml" };
                var hasHtmlExtension = fileExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                if (hasHtmlExtension)
                {
                    // High confidence for HTML file extensions
                    LogDetection("HTML file extension detected", 0.95, $"File: {fileName}");
                    return new DetectionResult
                    {
                        CanHandle = true,
                        Confidence = 0.95,
                        DocumentType = "HTML",
                        RecommendedStrategy = "HtmlStructureChunking",
                        Reasoning = "HTML file extension detected",
                        Metadata = new Dictionary<string, object>
                        {
                            ["FileName"] = fileName,
                            ["DetectionMethod"] = "FileExtension"
                        }
                    };
                }

                // If it has a non-HTML extension, likely not HTML
                var excludeExtensions = _config.ExcludeExtensions ??
                    new[] { ".pdf", ".docx", ".doc", ".txt", ".csv", ".xlsx", ".xls", ".json", ".xml" };

                if (excludeExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return DetectionResult.NoMatch;
                }
            }

            var scanContent = GetScanContent(content);

            // Check for HTML doctype or html tag using configured patterns
            var trimmedContent = scanContent.TrimStart();
            var hasDoctype = _patterns["DocType"].IsMatch(trimmedContent);
            var hasHtmlTag = _patterns["HtmlOpenTag"].IsMatch(trimmedContent);

            // Count various HTML indicators using configured patterns
            var headerCount = _patterns["HeaderTag"].Matches(scanContent).Count;
            var divWithClassCount = _patterns["DivWithClass"].Matches(scanContent).Count;
            var totalHtmlTags = _patterns["HtmlTag"].Matches(scanContent).Count;
            var mainContentCount = _patterns["MainContent"].Matches(scanContent).Count;
            var metaTagCount = _patterns["MetaTag"].Matches(scanContent).Count;
            var linkTagCount = _patterns["LinkTag"].Matches(scanContent).Count;

            // Calculate confidence
            var confidence = 0.0;
            var reasons = new List<string>();

            // Strong indicators
            if (hasDoctype)
            {
                confidence += 0.35;
                reasons.Add("DOCTYPE declaration present");
            }

            if (hasHtmlTag)
            {
                confidence += 0.3;
                reasons.Add("HTML tag present");
            }

            // Structural indicators
            if (headerCount >= _config.MinHeaderCount)
            {
                confidence += 0.2;
                reasons.Add($"{headerCount} header tags found");
            }
            else if (headerCount > 0)
            {
                confidence += 0.1;
                reasons.Add($"{headerCount} header tags found (below threshold)");
            }

            if (divWithClassCount >= _config.MinDivWithClass)
            {
                confidence += 0.15;
                reasons.Add($"{divWithClassCount} structured divs found");
            }
            else if (divWithClassCount > 0)
            {
                confidence += 0.05;
                reasons.Add($"{divWithClassCount} divs with classes found");
            }

            // Additional HTML-specific indicators
            if (mainContentCount > 0)
            {
                confidence += 0.1;
                reasons.Add($"Main content tags found ({mainContentCount})");
            }

            if (metaTagCount > 0)
            {
                confidence += 0.05;
                reasons.Add($"{metaTagCount} meta tags found");
            }

            if (linkTagCount > 0)
            {
                confidence += 0.05;
                reasons.Add($"{linkTagCount} link tags found");
            }

            // General HTML tag density check
            if (totalHtmlTags > 20)
            {
                confidence += 0.1;
                reasons.Add($"High HTML tag density ({totalHtmlTags} tags)");
            }
            else if (totalHtmlTags > 10)
            {
                confidence += 0.05;
                reasons.Add($"{totalHtmlTags} HTML tags found");
            }

            // Check for common HTML patterns
            if (_patterns["HeadSection"].IsMatch(scanContent))
            {
                confidence += 0.1;
                reasons.Add("Head section detected");
            }

            if (_patterns["BodyTag"].IsMatch(scanContent))
            {
                confidence += 0.1;
                reasons.Add("Body tag detected");
            }

            // Cap confidence at 1.0
            confidence = Math.Min(confidence, 1.0);

            // Determine if we can handle this document
            var minConfidence = _config.MinConfidence ?? 0.5;
            if (confidence >= minConfidence)
            {
                LogDetection("HTML detected", confidence, string.Join("; ", reasons));

                // Determine chunking strategy based on HTML complexity
                var recommendedStrategy = "HtmlStructureChunking";
                if (mainContentCount > 0 || divWithClassCount >= 10)
                {
                    recommendedStrategy = "HtmlDomSegmentation";
                    reasons.Add("Complex HTML structure - using DOM segmentation");
                }

                return new DetectionResult
                {
                    CanHandle = true,
                    Confidence = confidence,
                    DocumentType = "HTML",
                    RecommendedStrategy = recommendedStrategy,
                    Reasoning = string.Join("; ", reasons),
                    Metadata = new Dictionary<string, object>
                    {
                        ["HeaderCount"] = headerCount,
                        ["DivWithClassCount"] = divWithClassCount,
                        ["TotalHtmlTags"] = totalHtmlTags,
                        ["MainContentTags"] = mainContentCount,
                        ["MetaTags"] = metaTagCount,
                        ["LinkTags"] = linkTagCount,
                        ["HasDoctype"] = hasDoctype,
                        ["HasHtmlTag"] = hasHtmlTag
                    }
                };
            }

            // Not enough confidence to handle as HTML
            if (confidence > 0)
            {
                LogDetection("Insufficient HTML indicators", confidence, string.Join("; ", reasons));
            }

            return DetectionResult.NoMatch;
        }
    }
}
