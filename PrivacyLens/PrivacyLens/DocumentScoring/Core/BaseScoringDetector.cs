using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Models;
using PrivacyLens.DocumentScoring.FeatureExtraction;

namespace PrivacyLens.DocumentScoring.Core
{
    public abstract class BaseScoringDetector : IScoringDetector
    {
        protected readonly ILogger _logger;

        public abstract string DocumentType { get; }
        public virtual int Priority => 50;

        protected BaseScoringDetector(ILogger logger = null)
        {
            _logger = logger;
        }

        protected abstract ScoringProfile GetScoringProfile();

        public virtual bool CanHandleDocument(DocumentMetadata metadata)
        {
            return true;
        }

        public virtual DocumentConfidenceScore DetectWithScoring(
            string content,
            DocumentFeatures features,
            DocumentMetadata metadata)
        {
            var score = new DocumentConfidenceScore
            {
                DocumentType = DocumentType,
                Evidence = new List<ScoringEvidence>()
            };

            var profile = GetScoringProfile();

            ApplyDefinitiveFeatures(content, features, metadata, score, profile);
            ApplyStructuralFeatures(features, score, profile);
            ApplyLexicalFeatures(features, score, profile);

            var penalties = CalculatePenalties(features, profile);
            score.PenaltyScore = penalties;

            score.NormalizeScore(profile.MaxPossibleScore);

            return score;
        }

        protected virtual void ApplyDefinitiveFeatures(
            string content,
            DocumentFeatures features,
            DocumentMetadata metadata,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            foreach (var feature in profile.DefinitiveFeatures)
            {
                if (CheckFeaturePresence(content, features, metadata, feature.Key))
                {
                    var location = DetermineFeatureLocation(content, feature.Key);
                    var multiplier = ScoringLocationMultipliers.GetMultiplier(location);
                    var finalScore = feature.Value * multiplier;

                    score.DefinitiveScore += finalScore;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = feature.Key,
                        Tier = EvidenceTier.Definitive,
                        Location = location,
                        BaseWeight = feature.Value,
                        LocationMultiplier = multiplier,
                        FinalScore = finalScore
                    });
                }
            }
        }

        protected virtual void ApplyStructuralFeatures(
            DocumentFeatures features,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            foreach (var feature in profile.StructuralFeatures)
            {
                if (CheckStructuralFeature(features, feature.Key))
                {
                    score.StructuralScore += feature.Value;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = feature.Key,
                        Tier = EvidenceTier.Structural,
                        BaseWeight = feature.Value,
                        FinalScore = feature.Value
                    });
                }
            }
        }

        protected virtual void ApplyLexicalFeatures(
            DocumentFeatures features,
            DocumentConfidenceScore score,
            ScoringProfile profile)
        {
            foreach (var feature in profile.LexicalFeatures)
            {
                var frequency = GetFeatureFrequency(features, feature.Key);
                if (frequency > 0)
                {
                    var points = Math.Min(feature.Value * frequency, feature.Value * 3);
                    score.LexicalScore += points;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = feature.Key,
                        Tier = EvidenceTier.Lexical,
                        BaseWeight = feature.Value,
                        FinalScore = points,
                        Description = $"Found {frequency} occurrences"
                    });
                }
            }
        }

        protected virtual float CalculatePenalties(DocumentFeatures features, ScoringProfile profile)
        {
            float penalties = 0;

            foreach (var conflict in profile.ConflictingFeatures)
            {
                if (CheckStructuralFeature(features, conflict.Key))
                {
                    penalties += conflict.Value;
                }
            }

            foreach (var required in profile.RequiredFeatures)
            {
                if (!CheckStructuralFeature(features, required))
                {
                    penalties += 15;
                }
            }

            return penalties;
        }

        protected virtual bool CheckFeaturePresence(string content, DocumentFeatures features, DocumentMetadata metadata, string featureName)
        {
            if (metadata != null)
            {
                if (featureName.Equals("title", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(metadata.Title))
                    return metadata.Title.Contains(featureName, StringComparison.OrdinalIgnoreCase);

                if (metadata.ExtractedFields.ContainsKey(featureName))
                    return true;
            }

            return content.Contains(featureName, StringComparison.OrdinalIgnoreCase);
        }

        protected virtual bool CheckStructuralFeature(DocumentFeatures features, string featureName)
        {
            return featureName switch
            {
                "HasMetadataBlock" => features.HasMetadataBlock,
                "HasTableOfContents" => features.HasTableOfContents,
                "HasNumberedSections" => features.HasNumberedSections,
                "HasSignatureBlock" => features.HasSignatureBlock,
                "HasFillableFields" => features.HasFillableFields,
                "HasCodeBlocks" => features.HasCodeBlocks,
                "HasTables" => features.HasTables,
                _ => features.DetectedPatterns.Contains(featureName)
            };
        }

        protected virtual int GetFeatureFrequency(DocumentFeatures features, string featureName)
        {
            if (features.KeywordFrequencies.TryGetValue(featureName, out var frequency))
                return frequency;
            return 0;
        }

        protected virtual DocumentLocation DetermineFeatureLocation(string content, string feature)
        {
            var index = content.IndexOf(feature, StringComparison.OrdinalIgnoreCase);
            if (index == -1) return DocumentLocation.BodyText;

            var relativePosition = (float)index / content.Length;

            if (relativePosition <= 0.05f) return DocumentLocation.Title;
            if (relativePosition <= 0.1f) return DocumentLocation.FirstParagraph;
            if (relativePosition <= 0.15f) return DocumentLocation.HeaderFooter;
            if (relativePosition >= 0.8f) return DocumentLocation.DeepContent;

            return DocumentLocation.BodyText;
        }
    }
}