using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.FeatureExtraction
{
    public class DocumentFeatureExtractor
    {
        private readonly Dictionary<string, Regex> _compiledPatterns;

        public DocumentFeatureExtractor()
        {
            _compiledPatterns = InitializePatterns();
        }

        private Dictionary<string, Regex> InitializePatterns()
        {
            return new Dictionary<string, Regex>
            {
                ["numbered_section"] = new Regex(@"^\s*(\d+\.)+\s+\w+", RegexOptions.Multiline | RegexOptions.Compiled),
                ["policy_number"] = new Regex(@"(Policy\s*(Number|#|No\.?)|Document\s*(Number|#))\s*:?\s*[\w-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                ["effective_date"] = new Regex(@"Effective\s+Date\s*:?\s*[\d/\-\w\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                ["nist_control"] = new Regex(@"\b[A-Z]{2}-\d{1,2}\b", RegexOptions.Compiled),
                ["iso_control"] = new Regex(@"\bA\.\d{1,2}(\.\d{1,2})?\b", RegexOptions.Compiled),
                ["api_endpoint"] = new Regex(@"(GET|POST|PUT|DELETE|PATCH)\s+/[\w/\{\}]+", RegexOptions.Compiled),
                ["code_block"] = new Regex(@"```[\s\S]*?```", RegexOptions.Compiled),
                ["fillable_field"] = new Regex(@"_{3,}|\[_+\]|\(\s*\)", RegexOptions.Compiled),
                ["signature_block"] = new Regex(@"(Signature|Sign|Signed\s+by)\s*:?\s*_{3,}", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            };
        }

        public DocumentFeatures ExtractFeatures(string content, DocumentMetadata metadata = null)
        {
            var features = new DocumentFeatures();

            if (string.IsNullOrWhiteSpace(content))
                return features;

            ExtractStructuralFeatures(content, features);
            ExtractKeywordFrequencies(content, features);
            ExtractIdentifiers(content, features);
            ExtractContentPatterns(content, features);
            ExtractLinguisticCharacteristics(content, features);

            return features;
        }

        private void ExtractStructuralFeatures(string content, DocumentFeatures features)
        {
            var lines = content.Split('\n');

            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var trimmed = line.Trim();

                if (_compiledPatterns["numbered_section"].IsMatch(trimmed))
                {
                    features.SectionHeaders.Add(trimmed);
                    features.HasNumberedSections = true;
                }

                if (IsLikelyHeader(trimmed))
                {
                    features.SectionHeaders.Add(trimmed);
                }
            }

            features.HasMetadataBlock =
                _compiledPatterns["policy_number"].IsMatch(content) ||
                _compiledPatterns["effective_date"].IsMatch(content);

            features.HasTableOfContents =
                content.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase) ||
                (content.Contains("Contents", StringComparison.OrdinalIgnoreCase) && features.HasNumberedSections);
        }

        private void ExtractKeywordFrequencies(string content, DocumentFeatures features)
        {
            var lowerContent = content.ToLower();
            var words = Regex.Split(lowerContent, @"\W+")
                .Where(w => !string.IsNullOrEmpty(w) && w.Length > 2)
                .ToArray();

            var keywordGroups = new Dictionary<string, HashSet<string>>
            {
                ["policy"] = new HashSet<string> { "policy", "procedure", "regulation", "guideline", "standard" },
                ["technical"] = new HashSet<string> { "api", "endpoint", "database", "server", "client", "architecture" },
                ["security"] = new HashSet<string> { "security", "access", "control", "authentication", "encryption" },
                ["privacy"] = new HashSet<string> { "privacy", "personal", "data", "consent", "collect" },
                ["financial"] = new HashSet<string> { "financial", "budget", "revenue", "expense", "audit" },
                ["legal"] = new HashSet<string> { "shall", "must", "required", "mandatory", "prohibited" }
            };

            foreach (var word in words)
            {
                foreach (var group in keywordGroups)
                {
                    if (group.Value.Contains(word))
                    {
                        if (!features.KeywordFrequencies.ContainsKey(group.Key))
                            features.KeywordFrequencies[group.Key] = 0;
                        features.KeywordFrequencies[group.Key]++;

                        if (!features.KeywordFrequencies.ContainsKey(word))
                            features.KeywordFrequencies[word] = 0;
                        features.KeywordFrequencies[word]++;
                    }
                }
            }

            features.UsesPrescriptiveLanguage =
                features.KeywordFrequencies.GetValueOrDefault("shall", 0) > 0 ||
                features.KeywordFrequencies.GetValueOrDefault("must", 0) > 0;
        }

        private void ExtractIdentifiers(string content, DocumentFeatures features)
        {
            if (_compiledPatterns.TryGetValue("nist_control", out var nistPattern))
            {
                var matches = nistPattern.Matches(content);
                foreach (Match match in matches)
                    features.ControlIdentifiers.Add($"NIST:{match.Value}");
            }

            if (_compiledPatterns.TryGetValue("iso_control", out var isoPattern))
            {
                var matches = isoPattern.Matches(content);
                foreach (Match match in matches)
                    features.ControlIdentifiers.Add($"ISO:{match.Value}");
            }
        }

        private void ExtractContentPatterns(string content, DocumentFeatures features)
        {
            features.HasCodeBlocks = _compiledPatterns["code_block"].IsMatch(content);
            features.HasFillableFields = _compiledPatterns["fillable_field"].IsMatch(content);
            features.HasSignatureBlock = _compiledPatterns["signature_block"].IsMatch(content);
            features.HasTables = content.Contains("|") && content.Contains("-");
        }

        private void ExtractLinguisticCharacteristics(string content, DocumentFeatures features)
        {
            var sentences = Regex.Split(content, @"[.!?]+")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            if (sentences.Any())
            {
                var passivePattern = new Regex(@"\b(is|are|was|were|been|being)\s+\w+ed\b", RegexOptions.IgnoreCase);
                var passiveCount = passivePattern.Matches(content).Count;
                features.PassiveVoiceRatio = (float)passiveCount / sentences.Length;
            }
        }

        private bool IsLikelyHeader(string line)
        {
            if (line.Length > 100) return false;

            var headerKeywords = new[]
            {
                "Introduction", "Purpose", "Scope", "Background",
                "Objectives", "Definitions", "Requirements",
                "Responsibilities", "Procedures", "References"
            };

            return headerKeywords.Any(h =>
                line.StartsWith(h, StringComparison.OrdinalIgnoreCase));
        }
    }
}