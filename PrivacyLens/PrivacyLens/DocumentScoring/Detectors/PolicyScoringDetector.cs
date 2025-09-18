using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    /// <summary>
    /// Enhanced Policy Document Detector with improved accuracy
    /// Implements context validation and multi-evidence requirements
    /// </summary>
    public class PolicyScoringDetectorEnhanced : BaseScoringDetector
    {
        public override string DocumentType => "Policy & Legal";
        public override int Priority => 10;

        // Tier 1: Definitive identifiers with context validation
        private static readonly Regex AlphanumericIdentifierPattern = new Regex(
            @"(?:^|\n)\s*(?:Policy|Regulation|By-?law|AR|Administrative\s+Regulation)\s*(?:Number|#|No\.?)?[\s:]*([A-Z]{2,4}[-\s]?\d{3,4}(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // Title-based policy detection (stronger signal)
        private static readonly Regex TitlePolicyPattern = new Regex(
            @"(?:Policy|Procedure|Regulation|By-?law|Administrative\s+Regulation)[\s:]*([A-Z]{2,4}[-\s]?\d{3,4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Metadata block patterns
        private static readonly Regex PolicyMetadataPattern = new Regex(
            @"Policy\s*(?:Number|#|No\.?)[\s:]*[\w-]+.*?Effective\s+Date[\s:]*\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Negative indicators (reduce confidence)
        private static readonly HashSet<string> NonPolicyIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "announcement", "news", "article", "blog", "newsletter", "update",
            "notice", "alert", "reminder", "invitation", "agenda", "minutes",
            "report card", "calendar", "schedule", "event", "welcome"
        };

        // Navigation/footer patterns to exclude
        private static readonly Regex NavigationPattern = new Regex(
            @"(?:nav|menu|footer|header|breadcrumb|sitemap)[\s\-_]?(?:item|link|content)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Standard policy sections
        private static readonly HashSet<string> PolicySectionHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Purpose", "Scope", "Definitions", "Policy Statement",
            "Responsibilities", "Procedures", "Authority", "References",
            "Compliance", "Enforcement", "Exceptions", "Effective Date"
        };

        public PolicyScoringDetectorEnhanced(ILogger logger = null) : base(logger) { }

        public override DocumentConfidenceScore DetectWithScoring(
            string content,
            DocumentFeatures features,
            DocumentMetadata metadata)
        {
            var score = new DocumentConfidenceScore
            {
                DocumentType = DocumentType,
                Evidence = new List<ScoringEvidence>()
            };

            // Check for negative indicators first
            if (HasStrongNegativeIndicators(content, metadata, features))
            {
                _logger?.LogDebug("Document has strong negative indicators for policy classification");
                score.NormalizedConfidence = 0;
                return score;
            }

            // Extract main content (exclude likely navigation/footer)
            var mainContent = ExtractMainContent(content);

            // TIER 1: Check for definitive policy document markers
            if (CheckDefinitivePolicyMarkers(mainContent, metadata, score))
            {
                // Require additional evidence for Tier 1 classification
                var sectionCount = CountPolicySections(mainContent);
                if (sectionCount >= 3)
                {
                    score.DefinitiveScore = 100f;
                    score.NormalizedConfidence = 98f;

                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = $"Complete Policy Document ({sectionCount} sections)",
                        Value = "Definitive",
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.BodyText,
                        BaseWeight = 50f,
                        FinalScore = 50f
                    });

                    _logger?.LogInformation("Policy document identified with high confidence");
                    return score;
                }
            }

            // TIER 2: Weighted scoring
            var profile = GetScoringProfile();

            // Apply feature scoring with context validation
            ApplyContextualFeatureScoring(mainContent, features, metadata, score, profile);

            // Apply structural analysis
            ApplyStructuralScoring(mainContent, features, score, profile);

            // Apply penalties for negative indicators
            ApplyNegativePenalties(content, metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require minimum evidence count for classification
            if (score.Evidence.Count < 2 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(45, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for policy classification, capping confidence at 45%");
            }

            return score;
        }

        private bool HasStrongNegativeIndicators(string content, DocumentMetadata metadata, DocumentFeatures features)
        {
            // Check filename for non-policy indicators
            if (metadata?.FileName != null)
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (NonPolicyIndicators.Any(indicator => fileName.Contains(indicator)))
                {
                    _logger?.LogDebug("Filename suggests non-policy document: {FileName}", metadata.FileName);
                    return true;
                }
            }

            // Check title for non-policy indicators
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if (NonPolicyIndicators.Any(indicator => title.Contains(indicator)) &&
                    !title.Contains("policy") && !title.Contains("procedure"))
                {
                    _logger?.LogDebug("Title suggests non-policy document: {Title}", metadata.Title);
                    return true;
                }
            }

            // Check if it's primarily navigation/menu content
            var navMatches = NavigationPattern.Matches(content);
            if (navMatches.Count > 10)
            {
                _logger?.LogDebug("Document appears to be primarily navigation content");
                return true;
            }

            // Check for empty content after stripping HTML
            var textOnly = Regex.Replace(content, @"<[^>]+>", " ");
            if (textOnly.Trim().Length < 200)
            {
                _logger?.LogDebug("Document has insufficient text content");
                return true;
            }

            return false;
        }

        private string ExtractMainContent(string content)
        {
            // Simple heuristic: focus on the middle 80% of the document
            // This helps exclude headers/footers
            if (content.Length < 1000)
                return content;

            int startIndex = content.Length / 10;  // Skip first 10%
            int endIndex = content.Length - (content.Length / 10);  // Skip last 10%

            return content.Substring(startIndex, endIndex - startIndex);
        }

        private bool CheckDefinitivePolicyMarkers(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            // Check if the title contains a policy identifier
            if (metadata?.Title != null)
            {
                var titleMatch = TitlePolicyPattern.Match(metadata.Title);
                if (titleMatch.Success)
                {
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Policy Identifier in Title",
                        Value = titleMatch.Groups[1].Value,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 40f,
                        FinalScore = 40f
                    });

                    // Also check for metadata block
                    if (PolicyMetadataPattern.IsMatch(content))
                    {
                        score.Evidence.Add(new ScoringEvidence
                        {
                            Feature = "Complete Metadata Block",
                            Value = "Present",
                            Tier = EvidenceTier.Definitive,
                            Location = DocumentLocation.HeaderFooter,
                            BaseWeight = 30f,
                            FinalScore = 30f
                        });
                        score.DefinitiveScore = 70f; // Combined score
                        return true;
                    }
                }
            }

            // Check for policy identifier at the beginning of main content
            var contentMatch = AlphanumericIdentifierPattern.Match(content);
            if (contentMatch.Success && contentMatch.Index < 500)  // Must be near the beginning
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Policy Identifier at Document Start",
                    Value = contentMatch.Groups[1].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });

                // Check if it's a complete policy document
                if (PolicyMetadataPattern.IsMatch(content.Substring(0, Math.Min(2000, content.Length))))
                {
                    score.DefinitiveScore = 65f; // Combined score
                    return true;
                }
            }

            return false;
        }

        private int CountPolicySections(string content)
        {
            int count = 0;
            foreach (var section in PolicySectionHeaders)
            {
                // Look for section headers at the beginning of lines
                var pattern = $@"(?:^|\n)\s*{Regex.Escape(section)}\s*[:.\n]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    count++;
                }
            }
            return count;
        }

        private void ApplyContextualFeatureScoring(
            string content,
            DocumentFeatures features,
            DocumentMetadata metadata,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            // Count policy sections
            var sectionCount = CountPolicySections(content);
            if (sectionCount >= 5)
            {
                score.StructuralScore += 30f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Multiple Policy Sections ({sectionCount})",
                    Value = sectionCount.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 30f,
                    FinalScore = 30f
                });
            }
            else if (sectionCount >= 3)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Policy Sections ({sectionCount})",
                    Value = sectionCount.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for prescriptive language density
            var prescriptiveCount = CountPrescriptiveTerms(content);
            var wordCount = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            var prescriptiveDensity = (float)prescriptiveCount / Math.Max(wordCount, 1) * 1000;

            if (prescriptiveDensity > 5.0f)  // More than 5 prescriptive terms per 1000 words
            {
                score.LexicalScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "High Prescriptive Language Density",
                    Value = $"{prescriptiveDensity:F1} per 1000 words",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }
        }

        private void ApplyStructuralScoring(
            string content,
            DocumentFeatures features,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            // Check for hierarchical structure
            if (features.HasNumberedSections && features.HierarchicalDepth >= 2)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Hierarchical Document Structure",
                    Value = $"Depth {features.HierarchicalDepth}",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for formal document features
            if (features.HasMetadataBlock)
            {
                score.StructuralScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Formal Metadata Block",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.HeaderFooter,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }
        }

        private void ApplyNegativePenalties(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            float penalty = 0f;

            // Penalty for very short documents
            if (content.Length < 500)
            {
                penalty += 20f;
                _logger?.LogDebug("Applied penalty for short document length");
            }

            // Penalty for documents that look like lists or indexes
            if (content.Contains("Documents | ") && content.Length < 1000)
            {
                penalty += 30f;
                _logger?.LogDebug("Applied penalty for index/list page pattern");
            }

            // Penalty for high link density (suggests navigation page)
            var linkPattern = new Regex(@"https?://|href=|<a\s+", RegexOptions.IgnoreCase);
            var linkCount = linkPattern.Matches(content).Count;
            if (linkCount > 20)
            {
                penalty += 15f;
                _logger?.LogDebug("Applied penalty for high link density");
            }

            score.PenaltyScore = penalty;
        }

        private int CountPrescriptiveTerms(string content)
        {
            var prescriptiveTerms = new[] { "shall", "must", "will", "required", "mandatory", "prohibited" };
            int count = 0;

            foreach (var term in prescriptiveTerms)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                count += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            return count;
        }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 150f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Complete Policy Document"] = 100f,
                    ["Policy Identifier in Title"] = 40f,
                    ["Complete Metadata Block"] = 30f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Multiple Policy Sections"] = 30f,
                    ["Hierarchical Structure"] = 15f,
                    ["Formal Metadata"] = 25f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Prescriptive Language"] = 20f,
                    ["policy"] = 3f,
                    ["procedure"] = 3f,
                    ["compliance"] = 2f,
                    ["shall"] = 2f,
                    ["must"] = 2f
                }
            };
        }
    }
}