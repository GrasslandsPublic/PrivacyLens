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
    /// CRITICAL: Detector for Privacy Policies and Terms of Use documents
    /// These are the foundation of compliance assessment and require special care
    /// </summary>
    public class PrivacyTermsScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Privacy & Terms";
        public override int Priority => 5; // HIGHEST PRIORITY - even before Policy documents

        // Privacy-specific patterns
        private static readonly Regex PrivacyTitlePattern = new Regex(
            @"(?:Privacy\s+(?:Policy|Notice|Statement)|Data\s+(?:Privacy|Protection)\s+(?:Policy|Notice)|Personal\s+(?:Information|Data)\s+(?:Collection|Protection))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TermsTitlePattern = new Regex(
            @"(?:Terms\s+(?:of\s+(?:Use|Service)|and\s+Conditions)|User\s+Agreement|Service\s+Agreement|End\s+User\s+License\s+Agreement|EULA|Acceptable\s+Use\s+Policy)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataCollectionPattern = new Regex(
            @"(?:Information\s+We\s+Collect|Data\s+Collection|Types\s+of\s+(?:Information|Data)|Personal\s+(?:Information|Data)\s+(?:Collected|We\s+Collect))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataUsePattern = new Regex(
            @"(?:How\s+We\s+Use|Use\s+of\s+(?:Information|Data)|Purpose\s+of\s+(?:Collection|Processing)|Why\s+We\s+Collect)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataSharingPattern = new Regex(
            @"(?:(?:Information|Data)\s+Sharing|Third\s+Part(?:y|ies)|Disclosure\s+of\s+(?:Information|Data)|When\s+We\s+Share|With\s+Whom\s+We\s+Share)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataRetentionPattern = new Regex(
            @"(?:Data\s+Retention|Retention\s+(?:Period|Policy)|How\s+Long\s+We\s+(?:Keep|Store|Retain)|Storage\s+(?:Period|Duration))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UserRightsPattern = new Regex(
            @"(?:Your\s+(?:Rights|Choices)|User\s+Rights|Data\s+Subject\s+Rights|Right\s+to\s+(?:Access|Delete|Correct|Opt.?Out)|GDPR\s+Rights|CCPA\s+Rights)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CookiesPattern = new Regex(
            @"(?:Cookie\s+(?:Policy|Notice|Use)|Use\s+of\s+Cookies|Cookies\s+and\s+(?:Similar|Tracking)\s+Technologies)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SecurityPattern = new Regex(
            @"(?:(?:Data|Information)\s+Security|Security\s+(?:Measures|Practices)|How\s+We\s+Protect|Protection\s+of\s+(?:Information|Data))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CompliancePattern = new Regex(
            @"(?:GDPR|CCPA|COPPA|PIPEDA|FERPA|HIPAA|Privacy\s+Act|Data\s+Protection\s+Act|Compliance\s+with\s+(?:Law|Regulations))",
            RegexOptions.Compiled);

        private static readonly Regex LastUpdatedPattern = new Regex(
            @"(?:Last\s+(?:Updated|Modified|Revised)|Effective\s+(?:Date|as\s+of))[\s:]*(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Privacy & Terms section headers
        private static readonly HashSet<string> PrivacyTermsSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Privacy sections
            "Information We Collect", "Types of Information", "Personal Information",
            "How We Use Information", "Use of Data", "Purpose of Collection",
            "Information Sharing", "Third Parties", "Disclosure",
            "Data Retention", "How Long We Keep", "Storage Period",
            "Your Rights", "Your Choices", "User Rights", "Data Subject Rights",
            "Cookies", "Tracking Technologies", "Analytics",
            "Security", "Data Security", "Protection Measures",
            "Children's Privacy", "Children Under 13", "COPPA",
            "International Transfers", "Cross-Border Transfers",
            "Contact Us", "Contact Information", "Privacy Officer",
            
            // Terms sections
            "Acceptance of Terms", "Agreement to Terms", "Binding Agreement",
            "Use License", "Grant of License", "Permitted Use",
            "User Obligations", "User Responsibilities", "Prohibited Uses",
            "Intellectual Property", "Copyright", "Trademarks",
            "Disclaimers", "Limitation of Liability", "Indemnification",
            "Termination", "Suspension", "Account Termination",
            "Governing Law", "Jurisdiction", "Dispute Resolution",
            "Changes to Terms", "Modifications", "Updates"
        };

        // Privacy & Terms specific terminology
        private static readonly HashSet<string> PrivacyTermsTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Privacy terms
            "privacy", "personal information", "personal data", "data collection",
            "data processing", "data controller", "data processor", "consent",
            "opt-in", "opt-out", "cookies", "tracking", "analytics",
            "third party", "disclosure", "retention", "deletion", "access request",
            "data breach", "encryption", "anonymization", "pseudonymization",
            
            // Terms of use terms
            "terms", "conditions", "agreement", "license", "permitted",
            "prohibited", "restrictions", "obligations", "liability",
            "indemnify", "warranty", "disclaimer", "termination",
            "governing law", "jurisdiction", "arbitration", "dispute"
        };

        // Legal compliance indicators
        private static readonly HashSet<string> LegalComplianceTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GDPR", "CCPA", "COPPA", "PIPEDA", "FERPA", "HIPAA",
            "General Data Protection Regulation", "California Consumer Privacy Act",
            "Children's Online Privacy Protection Act", "Privacy Act",
            "Data Protection Act", "Privacy Shield", "Standard Contractual Clauses"
        };

        public PrivacyTermsScoringDetector(ILogger logger = null) : base(logger) { }

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

            // No negative checks for privacy docs - they're too important to miss

            // Check for strong privacy/terms indicators
            if (CheckStrongPrivacyTermsIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(98f, score.DefinitiveScore);
                _logger?.LogInformation("CRITICAL: Privacy/Terms document identified with high confidence");
                return score;
            }

            // Apply weighted scoring
            var profile = GetScoringProfile();

            // Check for privacy/terms patterns
            ApplyPrivacyTermsPatternScoring(content, score);

            // Check for privacy/terms sections
            ApplyPrivacyTermsSectionScoring(content, features, score);

            // Check for compliance indicators
            ApplyComplianceScoring(content, score);

            // Apply lexical analysis
            ApplyPrivacyTermsLexicalScoring(content, score);

            // Check metadata
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Lower threshold for privacy documents - we don't want to miss them
            if (score.Evidence.Count >= 2 && score.NormalizedConfidence < 50)
            {
                score.SetConfidence(50); // Boost to minimum 50% if we have some evidence
                _logger?.LogDebug("Privacy/Terms document with low confidence boosted to 50%");
            }

            return score;
        }

        private bool CheckStrongPrivacyTermsIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            int strongIndicators = 0;
            bool isPrivacy = false;
            bool isTerms = false;

            // Check for privacy policy title
            if (PrivacyTitlePattern.IsMatch(content))
            {
                isPrivacy = true;
                strongIndicators++;
                score.DefinitiveScore += 45f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Privacy Policy Title",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.Title,
                    BaseWeight = 45f,
                    FinalScore = 45f
                });
            }

            // Check for terms of use title
            if (TermsTitlePattern.IsMatch(content))
            {
                isTerms = true;
                strongIndicators++;
                score.DefinitiveScore += 45f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Terms of Use Title",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.Title,
                    BaseWeight = 45f,
                    FinalScore = 45f
                });
            }

            // Check for data collection and use sections (privacy indicator)
            bool hasDataCollection = DataCollectionPattern.IsMatch(content);
            bool hasDataUse = DataUsePattern.IsMatch(content);
            bool hasDataSharing = DataSharingPattern.IsMatch(content);

            if (hasDataCollection && hasDataUse)
            {
                strongIndicators++;
                score.DefinitiveScore += 40f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Data Collection & Use Sections",
                    Value = "Both Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
            }

            // Check for user rights section (strong privacy indicator)
            if (UserRightsPattern.IsMatch(content))
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "User Rights Section",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for compliance references
            var complianceMatches = CompliancePattern.Matches(content);
            if (complianceMatches.Count >= 2)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Legal Compliance References",
                    Value = $"{complianceMatches.Count} references",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check title metadata
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if (title.Contains("privacy") || title.Contains("terms of") ||
                    title.Contains("data protection") || title.Contains("cookie"))
                {
                    strongIndicators++;
                    score.DefinitiveScore += 30f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Privacy/Terms Document Title",
                        Value = metadata.Title,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });
                }
            }

            // Very strong evidence if both privacy and terms indicators present
            if (isPrivacy && isTerms)
            {
                score.DefinitiveScore = Math.Min(98f, score.DefinitiveScore);
                return true;
            }

            // Strong evidence threshold (lower than other detectors - we don't want to miss these)
            if (strongIndicators >= 2 || (strongIndicators >= 1 && score.DefinitiveScore >= 60))
            {
                return true;
            }

            return false;
        }

        private void ApplyPrivacyTermsPatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for cookies section
            if (CookiesPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Cookies Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for security section
            if (SecurityPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Security Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for data retention
            if (DataRetentionPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Data Retention Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for last updated date
            if (LastUpdatedPattern.IsMatch(content))
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Last Updated Date",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyPrivacyTermsSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int sectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in PrivacyTermsSections)
            {
                var pattern = $@"(?:^|\n)\s*\d*\.?\s*{Regex.Escape(section)}\s*[:.\n]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    sectionCount++;
                    foundSections.Add(section);
                    if (foundSections.Count > 8) break;
                }
            }

            if (sectionCount >= 3)
            {
                var points = Math.Min(40f, sectionCount * 8f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Privacy/Terms Sections ({sectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (sectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyComplianceScoring(string content, DocumentConfidenceScore score)
        {
            var complianceCount = 0;
            var foundCompliance = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in LegalComplianceTerms)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
                {
                    complianceCount++;
                    foundCompliance.Add(term);
                }
            }

            if (complianceCount >= 2)
            {
                var points = Math.Min(30f, complianceCount * 10f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Legal Compliance Terms ({complianceCount})",
                    Value = string.Join(", ", foundCompliance.Take(3)),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyPrivacyTermsLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

            var privacyTermCount = 0;
            var uniquePrivacyTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (PrivacyTermsTerms.Contains(word))
                {
                    privacyTermCount++;
                    uniquePrivacyTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate privacy/terms term density
            var density = (float)privacyTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 2.0f) // More than 2% privacy/terms terminology
            {
                var points = Math.Min(30f, density * 10f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Privacy/Terms Term Density",
                    Value = $"{density:F1}% ({uniquePrivacyTerms.Count} unique terms)",
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

            // Check filename for privacy/terms indicators
            if (!string.IsNullOrEmpty(metadata.FileName))
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("privacy") || fileName.Contains("terms") ||
                    fileName.Contains("tos") || fileName.Contains("eula") ||
                    fileName.Contains("cookie") || fileName.Contains("gdpr") ||
                    fileName.Contains("ccpa") || fileName.Contains("data-protection"))
                {
                    score.StructuralScore += 25f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Privacy/Terms Filename",
                        Value = metadata.FileName,
                        Tier = EvidenceTier.Structural,
                        Location = DocumentLocation.Title,
                        BaseWeight = 25f,
                        FinalScore = 25f
                    });
                }
            }
        }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 220f, // Higher max to ensure these get priority

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Privacy Policy Title"] = 45f,
                    ["Terms of Use Title"] = 45f,
                    ["Data Collection & Use"] = 40f,
                    ["User Rights Section"] = 35f,
                    ["Legal Compliance References"] = 35f,
                    ["Privacy/Terms Title"] = 30f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Privacy/Terms Sections"] = 40f,
                    ["Legal Compliance Terms"] = 30f,
                    ["Privacy/Terms Filename"] = 25f,
                    ["Cookies Section"] = 20f,
                    ["Security Section"] = 20f,
                    ["Data Retention"] = 20f,
                    ["Last Updated Date"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Privacy/Terms Density"] = 30f,
                    ["privacy"] = 4f,
                    ["personal information"] = 4f,
                    ["data collection"] = 3f,
                    ["consent"] = 3f,
                    ["terms"] = 3f,
                    ["cookies"] = 2f,
                    ["third party"] = 2f
                }
            };
        }
    }
}
