using System;
using System.Collections.Generic;

namespace PrivacyLens.DocumentScoring.Models
{
    public enum ConfidenceLevel
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh
    }

    public enum EvidenceTier
    {
        Definitive = 1,
        Structural = 2,
        Lexical = 3
    }

    public enum DocumentLocation
    {
        Title,
        MetadataBlock,
        FirstParagraph,
        SectionHeader,
        HeaderFooter,
        BodyText,
        DeepContent
    }

    public class DocumentConfidenceScore
    {
        public string DocumentType { get; set; }
        public float DefinitiveScore { get; set; }
        public float StructuralScore { get; set; }
        public float LexicalScore { get; set; }
        public float PenaltyScore { get; set; }

        public List<ScoringEvidence> Evidence { get; set; } = new List<ScoringEvidence>();

        public float RawScore => DefinitiveScore + StructuralScore + LexicalScore - PenaltyScore;
        public float NormalizedConfidence { get; set; } // Changed from private set to public set

        public ConfidenceLevel Level => NormalizedConfidence switch
        {
            >= 95 => ConfidenceLevel.VeryHigh,
            >= 85 => ConfidenceLevel.High,
            >= 70 => ConfidenceLevel.Medium,
            >= 50 => ConfidenceLevel.Low,
            _ => ConfidenceLevel.VeryLow
        };

        public void NormalizeScore(float maxPossibleScore)
        {
            if (maxPossibleScore <= 0)
            {
                NormalizedConfidence = 0;
                return;
            }

            NormalizedConfidence = Math.Min(100f, (RawScore / maxPossibleScore) * 100f);
        }

        public bool MeetsThreshold(float threshold = 85f)
        {
            return NormalizedConfidence >= threshold;
        }

        public void SetConfidence(float confidence)
        {
            NormalizedConfidence = Math.Min(100f, Math.Max(0f, confidence));
        }
    }
}