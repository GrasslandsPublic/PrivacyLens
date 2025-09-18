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

        // ORIGINAL: Tier 1: Definitive identifiers with context validation (AR 4027, P031, etc.)
        private static readonly Regex AlphanumericIdentifierPattern = new Regex(
            @"(?:^|\n)\s*(?:Policy|Regulation|By-?law|AR|Administrative\s+Regulation)\s*(?:Number|#|No\.?)?[\s:]*([A-Z]{2,4}[-\s]?\d{3,4}(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // NEW: Simple numeric policy codes (Policy Code: 810, Policy #: 123)
        private static readonly Regex SimpleNumericPolicyPattern = new Regex(
            @"Policy\s*(?:Code|Number|#|No\.?)[\s:]*(\d{1,4}(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // NEW: Policy Title pattern (Policy Title: School Year)
        private static readonly Regex PolicyTitleFieldPattern = new Regex(
            @"Policy\s+Title[\s:]+(.+?)(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ORIGINAL: Title-based policy detection (stronger signal)
        private static readonly Regex TitlePolicyPattern = new Regex(
            @"(?:Policy|Procedure|Regulation|By-?law|Administrative\s+Regulation)[\s:]*([A-Z]{2,4}[-\s]?\d{3,4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ORIGINAL: Metadata block patterns
        private static readonly Regex PolicyMetadataPattern = new Regex(
            @"Policy\s*(?:Number|#|No\.?)[\s:]*[\w-]+.*?Effective\s+Date[\s:]*\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // NEW: Additional metadata fields pattern
        private static readonly Regex PolicyMetadataFieldsPattern = new Regex(
            @"(?:Cross\s+Reference|Legal\s+Reference|Adoption\s+Date|Amendment\s+Date|Effective\s+Date|Review\s+Date)[\s:]+",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

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
            "Compliance", "Enforcement", "Exceptions", "Effective Date",
            "Guidelines", "Policy", "Background", "Application"  // Added Guidelines and Policy
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
            bool foundDefinitiveMarker = false;

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
                        BaseWeight = 50f,
                        FinalScore = 50f
                    });
                    foundDefinitiveMarker = true;
                    _logger?.LogDebug("Found policy identifier in title: {Identifier}", titleMatch.Groups[1].Value);
                }
            }

            // ORIGINAL: Check for alphanumeric policy identifiers (AR 4027, P031, etc.)
            var alphaMatch = AlphanumericIdentifierPattern.Match(content);
            if (alphaMatch.Success)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Policy Alphanumeric Identifier",
                    Value = alphaMatch.Groups[1].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 50f,
                    FinalScore = 50f
                });
                foundDefinitiveMarker = true;
                _logger?.LogDebug("Found alphanumeric policy identifier: {Identifier}", alphaMatch.Groups[1].Value);
            }

            // NEW: Check for simple numeric policy codes (Policy Code: 810)
            var simpleMatch = SimpleNumericPolicyPattern.Match(content);
            if (simpleMatch.Success)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Policy Code",
                    Value = simpleMatch.Groups[1].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 50f,
                    FinalScore = 50f
                });
                foundDefinitiveMarker = true;
                _logger?.LogDebug("Found simple policy code: {Code}", simpleMatch.Groups[1].Value);

                // Also check for Policy Title field
                var policyTitleMatch = PolicyTitleFieldPattern.Match(content);
                if (policyTitleMatch.Success)
                {
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Policy Title Field",
                        Value = policyTitleMatch.Groups[1].Value.Trim(),
                        Tier = EvidenceTier.Structural,
                        Location = DocumentLocation.BodyText,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });
                    _logger?.LogDebug("Found policy title field: {Title}", policyTitleMatch.Groups[1].Value.Trim());
                }
            }

            // NEW: Check for multiple metadata fields
            var metadataFieldMatches = PolicyMetadataFieldsPattern.Matches(content);
            if (metadataFieldMatches.Count >= 2)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Policy Metadata Fields",
                    Value = $"{metadataFieldMatches.Count} fields found",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
                foundDefinitiveMarker = true;
                _logger?.LogDebug("Found {Count} policy metadata fields", metadataFieldMatches.Count);
            }

            // ORIGINAL: Check for metadata block
            var metadataBlockMatch = PolicyMetadataPattern.Match(content);
            if (metadataBlockMatch.Success)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Policy Metadata Block",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 45f,
                    FinalScore = 45f
                });
                foundDefinitiveMarker = true;
                _logger?.LogDebug("Found policy metadata block");
            }

            return foundDefinitiveMarker;
        }

        private int CountPolicySections(string content)
        {
            int count = 0;
            var contentLower = content.ToLowerInvariant();

            foreach (var section in PolicySectionHeaders)
            {
                // Look for section headers with colons or as standalone lines
                var pattern = $@"(?:^|\n)\s*{Regex.Escape(section)}\s*[:|\n]";
                if (Regex.IsMatch(contentLower, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
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
            // Check for policy-related terms in context
            var policyTerms = new[] { "policy", "procedure", "regulation", "guideline", "directive", "standard" };
            var termCount = policyTerms.Count(term =>
                Regex.IsMatch(content, $@"\b{term}\b", RegexOptions.IgnoreCase));

            if (termCount >= 3)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Policy Terminology",
                    Value = $"{termCount} terms",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for regulatory references
            if (Regex.IsMatch(content, @"\b(?:Act|Regulation|Statute|Law|Code)\b", RegexOptions.IgnoreCase))
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Regulatory References",
                    Value = "Present",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyStructuralScoring(
            string content,
            DocumentFeatures features,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            // Check for numbered sections
            var numberedSections = Regex.Matches(content, @"^\s*\d+\.\s+[A-Z]", RegexOptions.Multiline);
            if (numberedSections.Count >= 3)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Numbered Sections",
                    Value = $"{numberedSections.Count} sections",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // Check for subsections (a., b., c. or i., ii., iii.)
            var subsections = Regex.Matches(content, @"^\s*[a-z]\.\s+|\s+[ivx]+\.\s+", RegexOptions.Multiline);
            if (subsections.Count >= 5)
            {
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Subsections",
                    Value = $"{subsections.Count} found",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyNegativePenalties(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            // Already handled in HasStrongNegativeIndicators
            // This method can apply graduated penalties for weaker negative signals

            // Check for informal language
            var informalTerms = new[] { "hey", "hi there", "thanks", "cheers", "lol", "fyi" };
            var informalCount = informalTerms.Count(term =>
                Regex.IsMatch(content, $@"\b{term}\b", RegexOptions.IgnoreCase));

            if (informalCount >= 2)
            {
                score.SetConfidence(score.NormalizedConfidence * 0.8f);
                _logger?.LogDebug("Applied penalty for informal language");
            }
        }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 200f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Policy Identifier"] = 50f,
                    ["Policy Code"] = 50f,
                    ["Policy Metadata Block"] = 45f,
                    ["Administrative Regulation"] = 50f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Policy Sections"] = 40f,
                    ["Regulatory Framework"] = 35f,
                    ["Governance Structure"] = 30f,
                    ["Policy Title Field"] = 30f,
                    ["Policy Metadata Fields"] = 40f,
                    ["Numbered Sections"] = 25f,
                    ["Subsections"] = 15f,
                    ["Hierarchical Structure"] = 20f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["policy"] = 3f,
                    ["procedure"] = 3f,
                    ["regulation"] = 3f,
                    ["compliance"] = 2f,
                    ["authority"] = 2f,
                    ["governance"] = 2f,
                    ["shall"] = 1f,
                    ["must"] = 1f,
                    ["Policy Terminology"] = 20f,
                    ["Regulatory References"] = 15f,
                    ["Formal Language"] = 15f
                }
            };
        }
    }
}