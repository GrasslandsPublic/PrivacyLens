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
    /// Detector for forms, templates, and fillable documents
    /// </summary>
    public class FormsScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Forms & Templates";
        public override int Priority => 35; // After Financial (25) and Board (30)

        // Fillable field patterns
        private static readonly Regex UnderscoreFieldPattern = new Regex(
            @"_{3,}|(?:Name|Date|Signature|Address|Phone|Email|Title)\s*:?\s*_{3,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BracketFieldPattern = new Regex(
            @"\[\s*(?:X|✓|√|_)?\s*\]|\(\s*(?:X|✓|√|_)?\s*\)|(?:□|☐|☑|☒|○|●|◯|◉)",
            RegexOptions.Compiled);

        private static readonly Regex FormFieldPattern = new Regex(
            @"(?:Please\s+)?(?:Print|Type|Enter|Fill|Complete|Provide|Specify|Indicate|Check|Select|Circle)\s+(?:your|the|all|applicable)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RequiredFieldPattern = new Regex(
            @"(?:\*|†|\(?\*\)?)\s*(?:Required|Mandatory|Must\s+complete)|(?:Required|Mandatory)\s+(?:field|information)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SignatureBlockPattern = new Regex(
            @"(?:Signature|Signed|Authorized\s+by)\s*:?\s*_{10,}|_{10,}\s*(?:Date|Dated)\s*:?\s*_{5,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DateFieldPattern = new Regex(
            @"(?:Date|Dated)\s*:?\s*_{5,}|(?:MM|DD|YYYY|mm|dd|yyyy)/(?:MM|DD|YYYY|mm|dd|yyyy)|__/__/____",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FormNumberPattern = new Regex(
            @"Form\s+(?:No\.?|Number|#)\s*:?\s*[\w-]+|Form\s+[\w-]+\s*(?:\(?\d{2}/\d{2}\)?|\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InstructionPattern = new Regex(
            @"(?:Instructions|Directions|How\s+to\s+complete|Guidelines)\s*:?|Please\s+(?:complete|fill\s+out|submit|return|attach|include)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex OfficeUsePattern = new Regex(
            @"(?:For\s+)?Office\s+Use\s+Only|Do\s+not\s+write\s+(?:below|above|in)\s+this\s+(?:space|area|section)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Form section headers
        private static readonly HashSet<string> FormSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Personal Information", "Contact Information", "Applicant Information",
            "Student Information", "Employee Information", "Parent/Guardian Information",
            "Emergency Contact", "Medical Information", "Health Information",
            "Educational Background", "Employment History", "References",
            "Declaration", "Certification", "Authorization", "Consent",
            "Terms and Conditions", "Agreement", "Acknowledgement",
            "Submission Instructions", "Required Documents", "Attachments",
            "Payment Information", "Banking Information", "Insurance Information"
        };

        // Form-related terms
        private static readonly HashSet<string> FormTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application", "registration", "enrollment", "request", "claim",
            "petition", "submission", "form", "template", "questionnaire",
            "survey", "checklist", "worksheet", "evaluation", "assessment",
            "consent", "authorization", "declaration", "certification",
            "applicant", "respondent", "claimant", "petitioner",
            "checkbox", "checkmark", "initial", "sign", "date",
            "complete", "fill", "submit", "attach", "include"
        };

        // Terms that suggest it's NOT a form
        private static readonly HashSet<string> NonFormIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "motion", "carried", "seconded", // Board documents
            "revenue", "expense", "fiscal", // Financial documents
            "shall", "must", "prohibited", // Policy documents
            "api", "endpoint", "function" // Technical documents
        };

        public FormsScoringDetector(ILogger logger = null) : base(logger) { }

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
                _logger?.LogDebug("Document appears to be non-form");
                score.NormalizedConfidence = 0;
                return score;
            }

            // Check for strong form indicators
            if (CheckStrongFormIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(95f, score.DefinitiveScore);
                _logger?.LogInformation("Form/Template identified with high confidence");
                return score;
            }

            // Apply weighted scoring
            var profile = GetScoringProfile();

            // Check for form patterns
            ApplyFormPatternScoring(content, score);

            // Check for form sections
            ApplyFormSectionScoring(content, features, score);

            // Apply lexical analysis
            ApplyFormLexicalScoring(content, score);

            // Check metadata
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require minimum evidence for classification
            if (score.Evidence.Count < 2 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(40, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for form classification");
            }

            return score;
        }

        private bool IsProbablyOtherType(string content, DocumentMetadata metadata)
        {
            // Check for substantial narrative content (forms are usually sparse)
            var words = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var avgWordsPerLine = words.Length / Math.Max(content.Count(c => c == '\n'), 1);

            // Forms typically have short lines with fields
            if (avgWordsPerLine > 20)
            {
                _logger?.LogDebug("Document has too much narrative content for a form");
                return true;
            }

            // Check for other document type indicators
            int nonFormCount = 0;
            foreach (var term in NonFormIndicators)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                nonFormCount += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            if (nonFormCount > 10)
            {
                return true;
            }

            return false;
        }

        private bool CheckStrongFormIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            int strongIndicators = 0;

            // Check for form number
            var formNumbers = FormNumberPattern.Matches(content);
            if (formNumbers.Count > 0)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Form Number",
                    Value = formNumbers[0].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for multiple fillable fields
            var underscoreFields = UnderscoreFieldPattern.Matches(content);
            if (underscoreFields.Count >= 5)
            {
                strongIndicators++;
                score.DefinitiveScore += 40f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Fillable Fields ({underscoreFields.Count})",
                    Value = underscoreFields.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
            }

            // Check for checkboxes
            var checkboxes = BracketFieldPattern.Matches(content);
            if (checkboxes.Count >= 5)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Checkboxes ({checkboxes.Count})",
                    Value = checkboxes.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for signature blocks
            var signatures = SignatureBlockPattern.Matches(content);
            if (signatures.Count >= 1)
            {
                strongIndicators++;
                score.DefinitiveScore += 30f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Signature Block",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.DeepContent,
                    BaseWeight = 30f,
                    FinalScore = 30f
                });
            }

            // Check for office use only section
            if (OfficeUsePattern.IsMatch(content))
            {
                strongIndicators++;
                score.DefinitiveScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Office Use Only Section",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // Check title for form indicators
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if (title.Contains("form") || title.Contains("application") ||
                    title.Contains("registration") || title.Contains("template") ||
                    title.Contains("checklist") || title.Contains("worksheet"))
                {
                    strongIndicators++;
                    score.DefinitiveScore += 30f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Form Title",
                        Value = metadata.Title,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });
                }
            }

            // If we have multiple strong indicators, it's definitely a form
            if (strongIndicators >= 3 || (strongIndicators >= 2 && (underscoreFields.Count >= 10 || checkboxes.Count >= 10)))
            {
                return true;
            }

            return false;
        }

        private void ApplyFormPatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for form completion instructions
            var instructions = FormFieldPattern.Matches(content);
            if (instructions.Count >= 3)
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Completion Instructions ({instructions.Count})",
                    Value = instructions.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for required field markers
            var requiredFields = RequiredFieldPattern.Matches(content);
            if (requiredFields.Count >= 2)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Required Field Markers ({requiredFields.Count})",
                    Value = requiredFields.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for date fields
            var dateFields = DateFieldPattern.Matches(content);
            if (dateFields.Count >= 2)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Date Fields ({dateFields.Count})",
                    Value = dateFields.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for instruction sections
            if (InstructionPattern.IsMatch(content))
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Instruction Section",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyFormSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int sectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in FormSections)
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
                var points = Math.Min(25f, sectionCount * 5f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Form Sections ({sectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (sectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyFormLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

            var formTermCount = 0;
            var uniqueFormTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (FormTerms.Contains(word))
                {
                    formTermCount++;
                    uniqueFormTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate form term density
            var density = (float)formTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 2.0f) // More than 2% form terms
            {
                var points = Math.Min(20f, density * 5f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Form Term Density",
                    Value = $"{density:F1}% ({uniqueFormTerms.Count} unique terms)",
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

            // Check filename for form indicators
            if (!string.IsNullOrEmpty(metadata.FileName))
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("form") || fileName.Contains("application") ||
                    fileName.Contains("registration") || fileName.Contains("template") ||
                    fileName.Contains("checklist") || fileName.Contains("worksheet") ||
                    fileName.Contains("request") || fileName.Contains("consent"))
                {
                    score.StructuralScore += 20f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Form Filename Pattern",
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
                MaxPossibleScore = 180f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Fillable Fields"] = 40f,
                    ["Form Number"] = 35f,
                    ["Checkboxes"] = 35f,
                    ["Signature Block"] = 30f,
                    ["Form Title"] = 30f,
                    ["Office Use Only"] = 25f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Form Sections"] = 25f,
                    ["Completion Instructions"] = 20f,
                    ["Form Filename"] = 20f,
                    ["Required Fields"] = 15f,
                    ["Date Fields"] = 15f,
                    ["Instruction Section"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Form Terms"] = 20f,
                    ["application"] = 3f,
                    ["registration"] = 3f,
                    ["complete"] = 2f,
                    ["submit"] = 2f,
                    ["signature"] = 2f,
                    ["consent"] = 2f
                }
            };
        }
    }
}