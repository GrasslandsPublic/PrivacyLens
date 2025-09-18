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
    /// Detector for general reports including annual reports, progress reports, status reports
    /// Excludes financial reports (handled by FinancialScoringDetector)
    /// </summary>
    public class ReportScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Report";
        public override int Priority => 40; // After Forms (35), before general content

        // Report title and header patterns
        private static readonly Regex ReportTitlePattern = new Regex(
            @"(?:Annual|Quarterly|Monthly|Weekly|Progress|Status|Assessment|Evaluation|Performance|Activity|Summary)\s+Report",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExecutiveSummaryPattern = new Regex(
            @"(?:Executive\s+Summary|Management\s+Summary|Summary\s+of\s+Findings|Report\s+Summary|Overview)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReportPeriodPattern = new Regex(
            @"(?:Report(?:ing)?\s+Period|Period\s+(?:Covered|Ending)|For\s+the\s+(?:Year|Quarter|Month|Week)\s+(?:Ended|Ending))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FindingsPattern = new Regex(
            @"(?:Key\s+)?(?:Findings|Observations|Results|Outcomes|Conclusions|Discoveries)\s*:?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RecommendationsPattern = new Regex(
            @"(?:Recommendations|Suggested\s+Actions|Next\s+Steps|Action\s+Items|Proposed\s+Solutions)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MethodologyPattern = new Regex(
            @"(?:Methodology|Methods|Approach|Process|Procedures\s+Used|Data\s+Collection)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetricsPattern = new Regex(
            @"(?:Performance\s+)?(?:Metrics|Indicators|KPIs|Measures|Statistics|Data\s+Analysis)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AchievementsPattern = new Regex(
            @"(?:Key\s+)?(?:Achievements|Accomplishments|Successes|Milestones|Goals\s+(?:Met|Achieved))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChallengesPattern = new Regex(
            @"(?:Challenges|Issues|Problems|Obstacles|Barriers|Risks|Concerns)\s*(?:Identified|Encountered)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Report section headers
        private static readonly HashSet<string> ReportSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Executive Summary", "Introduction", "Background", "Objectives",
            "Scope", "Methodology", "Findings", "Results", "Analysis",
            "Discussion", "Conclusions", "Recommendations", "Next Steps",
            "Performance Overview", "Key Achievements", "Challenges",
            "Lessons Learned", "Best Practices", "Risk Assessment",
            "Impact Assessment", "Evaluation", "Outcomes", "Deliverables",
            "Timeline", "Progress Update", "Status Update", "Appendices",
            "Data Analysis", "Statistical Summary", "Trends", "Benchmarks"
        };

        // Report-related terms
        private static readonly HashSet<string> ReportTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "report", "summary", "analysis", "assessment", "evaluation",
            "review", "findings", "conclusions", "recommendations", "outcomes",
            "results", "performance", "progress", "status", "update",
            "metrics", "indicators", "data", "statistics", "trends",
            "achievements", "challenges", "objectives", "goals", "targets",
            "milestone", "deliverables", "timeline", "benchmark", "baseline"
        };

        // Terms that suggest it's a different type of report
        private static readonly HashSet<string> SpecializedReportIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Financial reports
            "financial", "budget", "fiscal", "revenue", "expense", "audit",
            // Board reports  
            "motion", "carried", "minutes", "trustees",
            // Technical reports
            "api", "endpoint", "technical specification",
            // Policy documents
            "shall", "must", "prohibited", "compliance"
        };

        public ReportScoringDetector(ILogger logger = null) : base(logger) { }

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

            // Check if this is a specialized report type
            if (IsSpecializedReport(content, metadata))
            {
                _logger?.LogDebug("Document appears to be a specialized report type");
                score.NormalizedConfidence = 0;
                return score;
            }

            // Check for strong report indicators
            if (CheckStrongReportIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(92f, score.DefinitiveScore);
                _logger?.LogInformation("Report document identified with high confidence");
                return score;
            }

            // Apply weighted scoring
            var profile = GetScoringProfile();

            // Check for report patterns
            ApplyReportPatternScoring(content, score);

            // Check for report sections
            ApplyReportSectionScoring(content, features, score);

            // Apply lexical analysis
            ApplyReportLexicalScoring(content, score);

            // Check metadata
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require minimum evidence for classification
            if (score.Evidence.Count < 2 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(40, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for report classification");
            }

            return score;
        }

        private bool IsSpecializedReport(string content, DocumentMetadata metadata)
        {
            // Count specialized terms
            var specializedCount = 0;
            var generalReportCount = 0;

            foreach (var term in SpecializedReportIndicators)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                specializedCount += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            foreach (var term in ReportTerms)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                generalReportCount += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            // If specialized terms significantly outweigh general report terms
            if (specializedCount > generalReportCount * 1.5 && specializedCount > 15)
            {
                return true;
            }

            // Check filename for specialized indicators
            if (metadata?.FileName != null)
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("financial") || fileName.Contains("budget") ||
                    fileName.Contains("minutes") || fileName.Contains("technical"))
                {
                    return true;
                }
            }

            return false;
        }

        private bool CheckStrongReportIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            int strongIndicators = 0;

            // Check for report title pattern
            var reportTitles = ReportTitlePattern.Matches(content);
            if (reportTitles.Count > 0)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Report Title Pattern",
                    Value = reportTitles[0].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.Title,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for executive summary
            if (ExecutiveSummaryPattern.IsMatch(content))
            {
                strongIndicators++;
                score.DefinitiveScore += 30f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Executive Summary",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 30f,
                    FinalScore = 30f
                });
            }

            // Check for findings and recommendations
            bool hasFindings = FindingsPattern.IsMatch(content);
            bool hasRecommendations = RecommendationsPattern.IsMatch(content);

            if (hasFindings && hasRecommendations)
            {
                strongIndicators++;
                score.DefinitiveScore += 40f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Findings and Recommendations",
                    Value = "Both Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
            }
            else if (hasFindings || hasRecommendations)
            {
                strongIndicators++;
                score.DefinitiveScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = hasFindings ? "Findings Section" : "Recommendations Section",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // Check for report period
            if (ReportPeriodPattern.IsMatch(content))
            {
                strongIndicators++;
                score.DefinitiveScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Report Period Specified",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // Check title metadata
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if ((title.Contains("report") && !title.Contains("financial")) ||
                    title.Contains("assessment") || title.Contains("evaluation") ||
                    title.Contains("analysis") || title.Contains("review"))
                {
                    strongIndicators++;
                    score.DefinitiveScore += 30f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Report Document Title",
                        Value = metadata.Title,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });
                }
            }

            // Need strong evidence for report classification
            if (strongIndicators >= 3 || (strongIndicators >= 2 && score.DefinitiveScore >= 60))
            {
                return true;
            }

            return false;
        }

        private void ApplyReportPatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for methodology section
            if (MethodologyPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Methodology Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for metrics/KPIs
            if (MetricsPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Metrics/KPIs Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for achievements
            if (AchievementsPattern.IsMatch(content))
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Achievements Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for challenges/issues
            if (ChallengesPattern.IsMatch(content))
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Challenges Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyReportSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int sectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in ReportSections)
            {
                var pattern = $@"(?:^|\n)\s*{Regex.Escape(section)}\s*[:.\n]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    sectionCount++;
                    foundSections.Add(section);
                    if (foundSections.Count > 6) break;
                }
            }

            if (sectionCount >= 4)
            {
                var points = Math.Min(35f, sectionCount * 7f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Report Sections ({sectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (sectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyReportLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

            var reportTermCount = 0;
            var uniqueReportTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (ReportTerms.Contains(word))
                {
                    reportTermCount++;
                    uniqueReportTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate report term density
            var density = (float)reportTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 1.5f) // More than 1.5% report terms
            {
                var points = Math.Min(25f, density * 8f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Report Term Density",
                    Value = $"{density:F1}% ({uniqueReportTerms.Count} unique terms)",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyMetadataScoring(DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            if (metadata == null) return;

            // Check filename for report indicators
            if (!string.IsNullOrEmpty(metadata.FileName))
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("report") || fileName.Contains("assessment") ||
                    fileName.Contains("evaluation") || fileName.Contains("analysis") ||
                    fileName.Contains("review") || fileName.Contains("summary"))
                {
                    // But exclude specialized reports
                    if (!fileName.Contains("financial") && !fileName.Contains("board") &&
                        !fileName.Contains("technical") && !fileName.Contains("audit"))
                    {
                        score.StructuralScore += 20f;
                        score.Evidence.Add(new ScoringEvidence
                        {
                            Feature = "Report Filename Pattern",
                            Value = metadata.FileName,
                            Tier = EvidenceTier.Structural,
                            Location = DocumentLocation.Title,
                            BaseWeight = 20f,
                            FinalScore = 20f
                        });
                    }
                }
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
                    ["Findings and Recommendations"] = 40f,
                    ["Report Title Pattern"] = 35f,
                    ["Executive Summary"] = 30f,
                    ["Report Document Title"] = 30f,
                    ["Report Period"] = 25f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Report Sections"] = 35f,
                    ["Methodology"] = 20f,
                    ["Metrics/KPIs"] = 20f,
                    ["Report Filename"] = 20f,
                    ["Achievements"] = 15f,
                    ["Challenges"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Report Terms"] = 25f,
                    ["findings"] = 3f,
                    ["recommendations"] = 3f,
                    ["analysis"] = 2f,
                    ["assessment"] = 2f,
                    ["outcomes"] = 2f,
                    ["performance"] = 2f
                }
            };
        }
    }
}
