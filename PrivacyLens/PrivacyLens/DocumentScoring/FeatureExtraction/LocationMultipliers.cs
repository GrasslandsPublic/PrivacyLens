using System.Collections.Generic;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.FeatureExtraction
{
    public static class ScoringLocationMultipliers
    {
        private static readonly Dictionary<DocumentLocation, float> Multipliers = new Dictionary<DocumentLocation, float>
        {
            { DocumentLocation.Title, 2.0f },
            { DocumentLocation.MetadataBlock, 1.5f },
            { DocumentLocation.FirstParagraph, 1.3f },
            { DocumentLocation.SectionHeader, 1.2f },
            { DocumentLocation.HeaderFooter, 1.2f },
            { DocumentLocation.BodyText, 1.0f },
            { DocumentLocation.DeepContent, 0.8f }
        };

        public static float GetMultiplier(DocumentLocation location)
        {
            return Multipliers.TryGetValue(location, out var multiplier) ? multiplier : 1.0f;
        }
    }
}