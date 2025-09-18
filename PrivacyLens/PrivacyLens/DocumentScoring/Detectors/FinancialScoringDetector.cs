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
    /// Detector for financial documents including budgets, statements, and reports
    /// </summary>
    public class FinancialScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Financial Document";
        public override int Priority => 25; // Between Technical (20) and Board (30)

        // Financial patterns
        private static readonly Regex CurrencyPattern = new Regex(
            @"\$[\d,]+(?:\.\d{2})?(?:\s*(?:million|billion|thousand|M|K|B))?\b|\b\d{1,3}(?:,\d{3})+(?:\.\d{2})?\s*(?:dollars?|CAD|USD)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PercentagePattern = new Regex(
            @"\b\d+(?:\.\d+)?%|\b\d+(?:\.\d+)?\s*percent",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FiscalYearPattern = new Regex(
            @"(?:FY|Fiscal\s+Year|Financial\s+Year)\s*\d{2,4}(?:[-/]\d{2,4})?|\b20\d{2}[-/]20\d{2}\s*(?:fiscal|financial|budget)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QuarterPattern = new Regex(
            @"(?:Q[1-4]|Quarter\s+[1-4]|First|Second|Third|Fourth)\s+Quarter(?:\s+20\d{2})?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FinancialStatementPattern = new Regex(
            @"(?:Income\s+Statement|Balance\s+Sheet|Cash\s+Flow|Statement\s+of\s+(?:Operations|Financial\s+Position|Changes|Earnings))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LineItemPattern = new Regex(
            @"^\s*(?:[A-Z][A-Za-z\s&]+)\s+[\$\(]?[\d,]+(?:\.\d{2})?[\)]?\s+[\$\(]?[\d,]+(?:\.\d{2})?[\)]?",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex TotalPattern = new Regex(
            @"(?:Total|Subtotal|Grand\s+Total|Net|Gross)\s*:?\s*\$?[\d,]+(?:\.\d{2})?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BudgetPattern = new Regex(
            @"(?:Budget(?:ed)?|Actual|Variance|Over|Under)\s*:?\s*\$?[\d,]+(?:\.\d{2})?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Financial section headers
        private static readonly HashSet<string> FinancialSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Executive Summary", "Financial Highlights", "Revenue", "Revenues",
            "Expenses", "Expenditures", "Assets", "Liabilities", "Equity",
            "Budget Summary", "Budget Overview", "Financial Performance",
            "Operating Results", "Financial Position", "Cash Position",
            "Investments", "Debt", "Reserves", "Fund Balance",
            "Notes to Financial Statements", "Auditor's Report",
            "Management Discussion and Analysis", "MD&A"
        };

        // Financial terms for lexical analysis
        private static readonly HashSet<string> FinancialTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "revenue", "expense", "income", "cost", "budget", "actual",
            "variance", "surplus", "deficit", "asset", "liability", "equity",
            "cash", "investment", "debt", "loan", "interest", "principal",
            "amortization", "depreciation", "accrual", "receivable", "payable",
            "fiscal", "financial", "audit", "audited", "unaudited",
            "quarter", "annual", "year-to-date", "ytd", "forecast",
            "allocation", "appropriation", "disbursement", "expenditure",
            "grant", "funding", "contribution", "donation", "subsidy"
        };

        // Terms that suggest it might be a different document type
        private static readonly HashSet<string> NonFinancialIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "motion", "seconded", "carried", // Board documents
            "api", "endpoint", "function", // Technical documents
            "shall", "must", "prohibited" // Policy documents
        };

        public FinancialScoringDetector(ILogger logger = null) : base(logger) { }

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

            // Check if this might be another document type
            if (IsProbablyOtherType(content, metadata))
            {
                _logger?.LogDebug("Document appears to be non-financial");
                score.NormalizedConfidence = 0;
                return score;
            }

            // Check for strong financial indicators
            if (CheckStrongFinancialIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(95f, score.DefinitiveScore);
                _logger?.LogInformation("Financial document identified with high confidence");
                return score;
            }

            // Apply weighted scoring
            var profile = GetScoringProfile();

            // Check for financial patterns
            ApplyFinancialPatternScoring(content, score);

            // Check for financial sections
            ApplyFinancialSectionScoring(content, features, score);

            // Apply lexical analysis
            ApplyFinancialLexicalScoring(content, score);

            // Check metadata
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require minimum evidence for classification
            if (score.Evidence.Count < 2 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(40, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for financial classification");
            }

            return score;
        }

        private bool IsProbablyOtherType(string content, DocumentMetadata metadata)
        {
            // Quick check for other document types
            int nonFinancialCount = 0;

            foreach (var term in NonFinancialIndicators)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                nonFinancialCount += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            // If we have many non-financial indicators, probably not a financial doc
            if (nonFinancialCount > 10)
            {
                return true;
            }

            return false;
        }

        private bool CheckStrongFinancialIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            int strongIndicators = 0;

            // Check for financial statements
            var statements = FinancialStatementPattern.Matches(content);
            if (statements.Count > 0)
            {
                strongIndicators++;
                score.DefinitiveScore += 40f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Financial Statement Type",
                    Value = statements[0].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
            }

            // Check for high density of currency amounts
            var currencyMatches = CurrencyPattern.Matches(content);
            if (currencyMatches.Count >= 10)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Multiple Currency Values ({currencyMatches.Count})",
                    Value = currencyMatches.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for fiscal year patterns
            var fiscalYears = FiscalYearPattern.Matches(content);
            if (fiscalYears.Count >= 2)
            {
                strongIndicators++;
                score.DefinitiveScore += 30f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Fiscal Year References",
                    Value = fiscalYears[0].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 30f,
                    FinalScore = 30f
                });
            }

            // Check for line items (financial table structure)
            var lineItems = LineItemPattern.Matches(content);
            if (lineItems.Count >= 5)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Financial Line Items ({lineItems.Count})",
                    Value = lineItems.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check title for financial indicators
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if (title.Contains("financial") || title.Contains("budget") ||
                    title.Contains("annual report") || title.Contains("financial statement") ||
                    title.Contains("audit") || title.Contains("revenue") ||
                    title.Contains("expense") || title.Contains("fiscal"))
                {
                    strongIndicators++;
                    score.DefinitiveScore += 30f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Financial Document Title",
                        Value = metadata.Title,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });
                }
            }

            // If we have multiple strong indicators, it's definitely financial
            if (strongIndicators >= 3 || (strongIndicators >= 2 && currencyMatches.Count >= 20))
            {
                return true;
            }

            return false;
        }

        private void ApplyFinancialPatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for budget comparisons
            var budgetComparisons = BudgetPattern.Matches(content);
            if (budgetComparisons.Count >= 3)
            {
                score.StructuralScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Budget Comparisons ({budgetComparisons.Count})",
                    Value = budgetComparisons.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // Check for totals/subtotals
            var totals = TotalPattern.Matches(content);
            if (totals.Count >= 3)
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Total/Subtotal Lines ({totals.Count})",
                    Value = totals.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for percentages (common in financial analysis)
            var percentages = PercentagePattern.Matches(content);
            if (percentages.Count >= 5)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Percentage Values ({percentages.Count})",
                    Value = percentages.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for quarterly references
            var quarters = QuarterPattern.Matches(content);
            if (quarters.Count >= 2)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Quarterly References",
                    Value = quarters[0].Value,
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyFinancialSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int sectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in FinancialSections)
            {
                var pattern = $@"(?:^|\n)\s*{Regex.Escape(section)}\s*[:.\n]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    sectionCount++;
                    foundSections.Add(section);
                    if (foundSections.Count > 5) break;
                }
            }

            if (sectionCount >= 3)
            {
                var points = Math.Min(30f, sectionCount * 7f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Financial Sections ({sectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (sectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyFinancialLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

            var financialTermCount = 0;
            var uniqueFinancialTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (FinancialTerms.Contains(word))
                {
                    financialTermCount++;
                    uniqueFinancialTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate financial term density
            var density = (float)financialTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 1.5f) // More than 1.5% financial terms
            {
                var points = Math.Min(25f, density * 8f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Financial Term Density",
                    Value = $"{density:F1}% ({uniqueFinancialTerms.Count} unique terms)",
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

            // Check filename for financial indicators
            if (!string.IsNullOrEmpty(metadata.FileName))
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("budget") || fileName.Contains("financial") ||
                    fileName.Contains("audit") || fileName.Contains("expense") ||
                    fileName.Contains("revenue") || fileName.Contains("annual") && fileName.Contains("report") ||
                    fileName.Contains("aerr") || fileName.Contains("aer"))
                {
                    score.StructuralScore += 20f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Financial Filename Pattern",
                        Value = metadata.FileName,
                        Tier = EvidenceTier.Structural,
                        Location = DocumentLocation.Title,
                        BaseWeight = 20f,
                        FinalScore = 20f
                    });
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
                    ["Financial Statement"] = 40f,
                    ["Multiple Currency Values"] = 35f,
                    ["Financial Line Items"] = 35f,
                    ["Fiscal Year References"] = 30f,
                    ["Financial Title"] = 30f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Financial Sections"] = 30f,
                    ["Budget Comparisons"] = 25f,
                    ["Total Lines"] = 20f,
                    ["Financial Filename"] = 20f,
                    ["Percentage Values"] = 15f,
                    ["Quarterly References"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Financial Terms"] = 25f,
                    ["revenue"] = 3f,
                    ["expense"] = 3f,
                    ["budget"] = 3f,
                    ["fiscal"] = 2f,
                    ["audit"] = 2f,
                    ["surplus"] = 2f,
                    ["deficit"] = 2f
                }
            };
        }
    }
}