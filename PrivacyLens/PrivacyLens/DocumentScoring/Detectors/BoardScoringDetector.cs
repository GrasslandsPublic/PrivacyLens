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
    /// Detector for board documents including meeting minutes, agendas, and resolutions
    /// Carefully distinguishes from policy documents
    /// </summary>
    public class BoardDocumentScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Board Documents";
        public override int Priority => 30; // Lower priority than Policy and Technical

        // Meeting-specific patterns
        private static readonly Regex MeetingDatePattern = new Regex(
            @"(?:Meeting\s+(?:Date|Held|of)|Minutes\s+of|Meeting\s+Minutes)[\s:]*(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CallToOrderPattern = new Regex(
            @"(?:Called?\s+to\s+Order|Meeting\s+Called\s+to\s+Order|The\s+meeting\s+was\s+called\s+to\s+order)(?:\s+at\s+\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MotionPattern = new Regex(
            @"(?:MOTION|Motion|MOVED|Moved)\s*(?:by|:)?\s*(?:Trustee|Director|Councillor|Member|Commissioner)?\s*[\w\s]+(?:,\s*)?(?:seconded|SECONDED|Seconded)\s*(?:by|:)?\s*(?:Trustee|Director|Councillor|Member)?\s*[\w\s]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VotingPattern = new Regex(
            @"(?:Motion\s+)?(?:CARRIED|Carried|DEFEATED|Defeated|PASSED|Passed|APPROVED|Approved|FAILED|Failed)(?:\s+(?:unanimously|UNANIMOUSLY|Unanimously|\d+-\d+|\d+/\d+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AttendancePattern = new Regex(
            @"(?:PRESENT|Present|ATTENDANCE|Attendance|ATTENDEES|Attendees|Members\s+Present|Trustees\s+Present)[\s:]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AbsentPattern = new Regex(
            @"(?:ABSENT|Absent|REGRETS|Regrets|Excused|EXCUSED)[\s:]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AgendaItemPattern = new Regex(
            @"^\s*(?:\d+\.(?:\d+\.)?|[A-Z]\.)\s+[A-Z][A-Za-z\s]+(?::|$)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex AdjournmentPattern = new Regex(
            @"(?:Meeting\s+)?(?:Adjourn(?:ed|ment)?|ADJOURN(?:ED|MENT)?|The\s+meeting\s+(?:was\s+)?adjourned)(?:\s+at\s+\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Board-specific terminology
        private static readonly HashSet<string> BoardTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "trustee", "trustees", "board", "chair", "chairperson", "vice-chair",
            "secretary", "treasurer", "quorum", "governance", "superintendent",
            "director", "directors", "commissioner", "commissioners", "council",
            "committee", "delegation", "presentation", "in-camera", "public session"
        };

        // Meeting section headers
        private static readonly HashSet<string> MeetingSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Call to Order", "Land Acknowledgement", "Attendance", "Approval of Agenda",
            "Approval of Minutes", "Business Arising", "New Business", "Reports",
            "Delegations", "Presentations", "Correspondence", "Information Items",
            "Discussion Items", "Action Items", "In-Camera Session", "Public Session",
            "Question Period", "Announcements", "Next Meeting", "Adjournment"
        };

        // Negative indicators (suggests it's a policy, not minutes)
        private static readonly HashSet<string> PolicyIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shall", "must", "required", "prohibited", "mandatory",
            "compliance", "violation", "enforcement", "penalty"
        };

        public BoardDocumentScoringDetector(ILogger logger = null) : base(logger) { }

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

            // First, check if this might be a policy document instead
            if (IsProbablyPolicy(content, metadata))
            {
                _logger?.LogDebug("Document appears to be a policy rather than board document");
                score.NormalizedConfidence = 0;
                return score;
            }

            // Check for strong board document indicators
            if (CheckStrongBoardIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(95f, score.DefinitiveScore);
                _logger?.LogInformation("Board document identified with high confidence");
                return score;
            }

            // Apply weighted scoring
            var profile = GetScoringProfile();

            // Check for meeting patterns
            ApplyMeetingPatternScoring(content, score);

            // Check for board-specific sections
            ApplyBoardSectionScoring(content, features, score);

            // Apply lexical analysis
            ApplyBoardLexicalScoring(content, score);

            // Check metadata
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require strong evidence for board document classification
            if (score.Evidence.Count < 3 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(40, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for board document classification");
            }

            return score;
        }

        private bool IsProbablyPolicy(string content, DocumentMetadata metadata)
        {
            // Check if filename suggests policy
            if (metadata?.FileName != null)
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("policy") || fileName.Contains("procedure") ||
                    fileName.Contains("regulation") || fileName.Contains("code"))
                {
                    return true;
                }
            }

            // Count policy language vs meeting language
            int policyTermCount = 0;
            int meetingTermCount = 0;

            foreach (var term in PolicyIndicators)
            {
                var pattern = $@"\b{Regex.Escape(term)}\b";
                policyTermCount += Regex.Matches(content, pattern, RegexOptions.IgnoreCase).Count;
            }

            meetingTermCount += MotionPattern.Matches(content).Count * 5; // Motions are strong indicators
            meetingTermCount += VotingPattern.Matches(content).Count * 5;
            meetingTermCount += AttendancePattern.Matches(content).Count * 3;

            // If policy terms significantly outweigh meeting terms, it's probably a policy
            if (policyTermCount > meetingTermCount * 2 && policyTermCount > 10)
            {
                return true;
            }

            return false;
        }

        private bool CheckStrongBoardIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            int strongIndicators = 0;

            // Check for meeting date pattern
            var meetingDates = MeetingDatePattern.Matches(content);
            if (meetingDates.Count > 0)
            {
                strongIndicators++;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Meeting Date Pattern",
                    Value = meetingDates[0].Value,
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 30f,
                    FinalScore = 30f
                });
            }

            // Check for motions
            var motions = MotionPattern.Matches(content);
            if (motions.Count >= 2)
            {
                strongIndicators++;
                score.DefinitiveScore += 40f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Multiple Motions ({motions.Count})",
                    Value = motions.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });
            }

            // Check for voting patterns
            var votes = VotingPattern.Matches(content);
            if (votes.Count >= 2)
            {
                strongIndicators++;
                score.DefinitiveScore += 35f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Voting Records ({votes.Count})",
                    Value = votes.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 35f,
                    FinalScore = 35f
                });
            }

            // Check for attendance section
            if (AttendancePattern.IsMatch(content) && AbsentPattern.IsMatch(content))
            {
                strongIndicators++;
                score.DefinitiveScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Attendance Records",
                    Value = "Present/Absent lists",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }

            // If we have multiple strong indicators, it's definitely a board document
            if (strongIndicators >= 3 || (strongIndicators >= 2 && motions.Count >= 3))
            {
                score.DefinitiveScore = Math.Min(95f, score.DefinitiveScore);
                return true;
            }

            // Check title for strong indicators
            if (metadata?.Title != null)
            {
                var title = metadata.Title.ToLowerInvariant();
                if ((title.Contains("minutes") && (title.Contains("board") || title.Contains("meeting"))) ||
                    (title.Contains("board") && title.Contains("agenda")) ||
                    title.Contains("board highlights") ||
                    title.Contains("trustee"))
                {
                    score.DefinitiveScore += 30f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Board Document Title",
                        Value = metadata.Title,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });

                    if (strongIndicators >= 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ApplyMeetingPatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for call to order
            if (CallToOrderPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Call to Order",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.FirstParagraph,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for adjournment
            if (AdjournmentPattern.IsMatch(content))
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Adjournment",
                    Value = "Present",
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.DeepContent,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }

            // Check for agenda items
            var agendaItems = AgendaItemPattern.Matches(content);
            if (agendaItems.Count >= 3)
            {
                score.StructuralScore += 25f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Agenda Structure ({agendaItems.Count} items)",
                    Value = agendaItems.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 25f,
                    FinalScore = 25f
                });
            }
        }

        private void ApplyBoardSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int sectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in MeetingSections)
            {
                var pattern = $@"(?:^|\n)\s*{Regex.Escape(section)}\s*[:.\n]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    sectionCount++;
                    foundSections.Add(section);
                    if (foundSections.Count > 5) break; // Limit tracking
                }
            }

            if (sectionCount >= 3)
            {
                var points = Math.Min(30f, sectionCount * 6f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Meeting Sections ({sectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (sectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyBoardLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':' },
                StringSplitOptions.RemoveEmptyEntries);

            var boardTermCount = 0;
            var uniqueBoardTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (BoardTerms.Contains(word))
                {
                    boardTermCount++;
                    uniqueBoardTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate board term density
            var density = (float)boardTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 1.0f) // More than 1% board terms
            {
                var points = Math.Min(20f, density * 10f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Board Terminology Density",
                    Value = $"{density:F1}% ({uniqueBoardTerms.Count} unique terms)",
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

            // Check filename for board indicators
            if (!string.IsNullOrEmpty(metadata.FileName))
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if (fileName.Contains("minutes") || fileName.Contains("agenda") ||
                    fileName.Contains("board") || fileName.Contains("trustee") ||
                    fileName.Contains("meeting"))
                {
                    score.StructuralScore += 15f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Board Filename Pattern",
                        Value = metadata.FileName,
                        Tier = EvidenceTier.Structural,
                        Location = DocumentLocation.Title,
                        BaseWeight = 15f,
                        FinalScore = 15f
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
                    ["Multiple Motions"] = 40f,
                    ["Voting Records"] = 35f,
                    ["Meeting Date Pattern"] = 30f,
                    ["Board Document Title"] = 30f,
                    ["Attendance Records"] = 25f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Meeting Sections"] = 30f,
                    ["Agenda Structure"] = 25f,
                    ["Call to Order"] = 20f,
                    ["Adjournment"] = 20f,
                    ["Board Filename"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Board Terminology"] = 20f,
                    ["trustee"] = 2f,
                    ["motion"] = 3f,
                    ["carried"] = 3f,
                    ["quorum"] = 2f,
                    ["delegation"] = 2f
                }
            };
        }
    }
}