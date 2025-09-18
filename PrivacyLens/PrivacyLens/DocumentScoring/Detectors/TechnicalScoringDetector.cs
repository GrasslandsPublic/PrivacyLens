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
    /// Detector for technical documentation including API docs, system guides, and code documentation
    /// </summary>
    public class TechnicalScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Technical Documentation";
        public override int Priority => 20; // Lower priority than Policy

        // Code block patterns
        private static readonly Regex CodeBlockPattern = new Regex(
            @"```[\w]*\n[\s\S]*?```|<code>[\s\S]*?</code>|<pre>[\s\S]*?</pre>",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // API endpoint patterns
        private static readonly Regex ApiEndpointPattern = new Regex(
            @"(?:GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+(?:/[\w/\{\}:\-\.]+)|(?:https?://[\w\./]+/api/[\w/]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Configuration and code patterns
        private static readonly Regex JsonConfigPattern = new Regex(
            @"\{[\s]*[""'][\w]+[""'][\s]*:[\s]*(?:[""'][\w\s]*[""']|[\d\.]+|true|false|null|\{|\[)",
            RegexOptions.Compiled);

        private static readonly Regex XmlTagPattern = new Regex(
            @"<(?!(?:html|body|head|div|span|p|a|img|table|tr|td|h[1-6])\b)[\w]+(?:\s+[\w:]+=[""'][\w\s]*[""'])*\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Programming patterns
        private static readonly Regex FunctionPattern = new Regex(
            @"(?:function|def|public|private|protected|static|async|void|int|string|bool|float|double|var|let|const)\s+\w+\s*\([^)]*\)",
            RegexOptions.Compiled);

        private static readonly Regex ImportPattern = new Regex(
            @"(?:import|using|require|include|from)\s+[\w\.\*\{\}]+|namespace\s+[\w\.]+",
            RegexOptions.Compiled);

        // Version and technical identifiers
        private static readonly Regex VersionPattern = new Regex(
            @"(?:v|version\s*)[0-9]+(?:\.[0-9]+)*(?:[-\w\.]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TechnicalIdentifierPattern = new Regex(
            @"(?:SDK|API|CLI|GUI|REST|SOAP|JSON|XML|SQL|NoSQL|OAuth|JWT|CORS|HTTPS?|FTP|SSH|TCP|UDP|IP)",
            RegexOptions.Compiled);

        // Command line patterns
        private static readonly Regex CommandLinePattern = new Regex(
            @"^\s*[\$#>]\s*[\w\-]+(?:\s+[\w\-\./=]+)*|npm\s+(?:install|run)|pip\s+install|dotnet\s+(?:run|build)|git\s+\w+",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Technical section headers
        private static readonly HashSet<string> TechnicalSectionHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Installation", "Setup", "Configuration", "API Reference", "API Documentation",
            "Getting Started", "Quick Start", "Prerequisites", "Requirements",
            "Usage", "Examples", "Code Examples", "Sample Code", "Implementation",
            "Parameters", "Arguments", "Options", "Environment Variables",
            "Endpoints", "Routes", "Methods", "Functions", "Classes",
            "Error Codes", "Status Codes", "Troubleshooting", "Debugging",
            "Authentication", "Authorization", "Security", "Permissions",
            "Database Schema", "Data Model", "Architecture", "System Design",
            "Dependencies", "Package Management", "Build Instructions", "Deployment"
        };

        // Technical terms for lexical analysis
        private static readonly HashSet<string> TechnicalTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "api", "endpoint", "request", "response", "payload", "header", "token",
            "authentication", "authorization", "oauth", "jwt", "bearer",
            "database", "query", "schema", "table", "index", "migration",
            "server", "client", "backend", "frontend", "middleware",
            "deployment", "docker", "kubernetes", "container", "microservice",
            "repository", "branch", "commit", "merge", "pull request",
            "configuration", "environment", "variable", "parameter", "setting",
            "debug", "error", "exception", "stack trace", "log", "logging",
            "async", "await", "promise", "callback", "event", "listener",
            "class", "object", "method", "function", "interface", "module",
            "package", "library", "framework", "dependency", "version"
        };

        public TechnicalScoringDetector(ILogger logger = null) : base(logger) { }

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

            // Check for strong technical indicators first
            if (CheckStrongTechnicalIndicators(content, metadata, score))
            {
                score.NormalizedConfidence = Math.Min(95f, score.DefinitiveScore);
                _logger?.LogInformation("Technical document identified with high confidence");
                return score;
            }

            // Apply weighted scoring for technical features
            var profile = GetScoringProfile();

            // Check for code blocks and API patterns
            ApplyCodePatternScoring(content, score);

            // Check for technical sections
            ApplyTechnicalSectionScoring(content, features, score);

            // Apply lexical analysis for technical terms
            ApplyTechnicalLexicalScoring(content, score);

            // Check metadata and filename
            ApplyMetadataScoring(metadata, score);

            // Normalize the score
            score.NormalizeScore(profile.MaxPossibleScore);

            // Require minimum evidence for classification
            if (score.Evidence.Count < 2 && score.NormalizedConfidence > 50)
            {
                score.SetConfidence(Math.Min(40, score.NormalizedConfidence));
                _logger?.LogDebug("Insufficient evidence for technical classification, capping confidence");
            }

            return score;
        }

        private bool CheckStrongTechnicalIndicators(string content, DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            // Check for multiple code blocks
            var codeBlocks = CodeBlockPattern.Matches(content);
            if (codeBlocks.Count >= 3)
            {
                score.DefinitiveScore = 85f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Multiple Code Blocks ({codeBlocks.Count})",
                    Value = codeBlocks.Count.ToString(),
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 40f,
                    FinalScore = 40f
                });

                // If we also have API endpoints, very strong signal
                var apiEndpoints = ApiEndpointPattern.Matches(content);
                if (apiEndpoints.Count >= 2)
                {
                    score.DefinitiveScore = 95f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = $"API Endpoints ({apiEndpoints.Count})",
                        Value = apiEndpoints.Count.ToString(),
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.BodyText,
                        BaseWeight = 35f,
                        FinalScore = 35f
                    });
                    return true;
                }
            }

            // Check for OpenAPI/Swagger specification
            if (content.Contains("openapi:", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("swagger:", StringComparison.OrdinalIgnoreCase))
            {
                score.DefinitiveScore = 98f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "OpenAPI/Swagger Specification",
                    Value = "Present",
                    Tier = EvidenceTier.Definitive,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 50f,
                    FinalScore = 50f
                });
                return true;
            }

            // Check filename for strong technical indicators
            if (metadata?.FileName != null)
            {
                var fileName = metadata.FileName.ToLowerInvariant();
                if ((fileName.Contains("api") && (fileName.Contains("doc") || fileName.Contains("reference"))) ||
                    (fileName.Contains("technical") && fileName.Contains("spec")) ||
                    (fileName.Contains("developer") && fileName.Contains("guide")) ||
                    fileName.EndsWith(".yaml") || fileName.EndsWith(".yml") ||
                    (fileName.EndsWith(".json") && fileName.Contains("config")))
                {
                    score.DefinitiveScore = 75f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Technical Filename Pattern",
                        Value = fileName,
                        Tier = EvidenceTier.Definitive,
                        Location = DocumentLocation.Title,
                        BaseWeight = 30f,
                        FinalScore = 30f
                    });

                    // Combined with code content = strong signal
                    if (codeBlocks.Count > 0 || ApiEndpointPattern.Matches(content).Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ApplyCodePatternScoring(string content, DocumentConfidenceScore score)
        {
            // Check for code blocks
            var codeBlocks = CodeBlockPattern.Matches(content);
            if (codeBlocks.Count > 0)
            {
                var points = Math.Min(30f, codeBlocks.Count * 10f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Code Blocks ({codeBlocks.Count})",
                    Value = codeBlocks.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }

            // Check for API endpoints
            var apiEndpoints = ApiEndpointPattern.Matches(content);
            if (apiEndpoints.Count > 0)
            {
                var points = Math.Min(25f, apiEndpoints.Count * 5f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"API Endpoints ({apiEndpoints.Count})",
                    Value = apiEndpoints.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }

            // Check for JSON configuration
            var jsonConfigs = JsonConfigPattern.Matches(content);
            if (jsonConfigs.Count >= 3)
            {
                score.StructuralScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "JSON Configuration Examples",
                    Value = jsonConfigs.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }

            // Check for command line examples
            var commands = CommandLinePattern.Matches(content);
            if (commands.Count >= 2)
            {
                score.StructuralScore += 20f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Command Line Examples ({commands.Count})",
                    Value = commands.Count.ToString(),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 20f,
                    FinalScore = 20f
                });
            }
        }

        private void ApplyTechnicalSectionScoring(string content, DocumentFeatures features, DocumentConfidenceScore score)
        {
            int technicalSectionCount = 0;
            var foundSections = new List<string>();

            foreach (var section in TechnicalSectionHeaders)
            {
                // Look for section headers
                var pattern = $@"(?:^|\n)\s*#*\s*{Regex.Escape(section)}\s*[:.\n#]";
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    technicalSectionCount++;
                    foundSections.Add(section);
                    if (foundSections.Count <= 3) // Only track first few
                    {
                        continue;
                    }
                }
            }

            if (technicalSectionCount >= 3)
            {
                var points = Math.Min(30f, technicalSectionCount * 8f);
                score.StructuralScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Technical Sections ({technicalSectionCount})",
                    Value = string.Join(", ", foundSections.Take(3)) + (technicalSectionCount > 3 ? "..." : ""),
                    Tier = EvidenceTier.Structural,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }
        }

        private void ApplyTechnicalLexicalScoring(string content, DocumentConfidenceScore score)
        {
            var words = content.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}' },
                StringSplitOptions.RemoveEmptyEntries);

            var technicalTermCount = 0;
            var uniqueTechnicalTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var word in words)
            {
                if (TechnicalTerms.Contains(word))
                {
                    technicalTermCount++;
                    uniqueTechnicalTerms.Add(word.ToLowerInvariant());
                }
            }

            // Calculate technical term density
            var density = (float)technicalTermCount / Math.Max(words.Length, 1) * 100;

            if (density > 2.0f) // More than 2% technical terms
            {
                var points = Math.Min(25f, density * 5f);
                score.LexicalScore += points;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Technical Term Density",
                    Value = $"{density:F1}% ({uniqueTechnicalTerms.Count} unique terms)",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = points,
                    FinalScore = points
                });
            }

            // Check for version numbers
            var versions = VersionPattern.Matches(content);
            if (versions.Count >= 3)
            {
                score.LexicalScore += 10f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = $"Version References ({versions.Count})",
                    Value = versions.Count.ToString(),
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 10f,
                    FinalScore = 10f
                });
            }

            // Check for technical acronyms
            var technicalIds = TechnicalIdentifierPattern.Matches(content);
            if (technicalIds.Count >= 5)
            {
                score.LexicalScore += 15f;
                score.Evidence.Add(new ScoringEvidence
                {
                    Feature = "Technical Acronyms/Identifiers",
                    Value = $"{technicalIds.Count} occurrences",
                    Tier = EvidenceTier.Lexical,
                    Location = DocumentLocation.BodyText,
                    BaseWeight = 15f,
                    FinalScore = 15f
                });
            }
        }

        private void ApplyMetadataScoring(DocumentMetadata metadata, DocumentConfidenceScore score)
        {
            if (metadata == null) return;

            // Check title for technical indicators
            if (!string.IsNullOrEmpty(metadata.Title))
            {
                var title = metadata.Title.ToLowerInvariant();
                if (title.Contains("api") || title.Contains("technical") ||
                    title.Contains("developer") || title.Contains("documentation") ||
                    (title.Contains("guide") && (title.Contains("system") || title.Contains("integration"))))
                {
                    score.StructuralScore += 20f;
                    score.Evidence.Add(new ScoringEvidence
                    {
                        Feature = "Technical Title Pattern",
                        Value = metadata.Title,
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
                    ["OpenAPI/Swagger Spec"] = 50f,
                    ["Multiple Code Blocks"] = 40f,
                    ["API Documentation"] = 45f,
                    ["Technical Specification"] = 40f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Code Examples"] = 30f,
                    ["API Endpoints"] = 25f,
                    ["Command Line Examples"] = 20f,
                    ["Technical Sections"] = 30f,
                    ["Configuration Examples"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["Technical Terms"] = 25f,
                    ["Version Numbers"] = 10f,
                    ["Technical Acronyms"] = 15f,
                    ["api"] = 3f,
                    ["endpoint"] = 3f,
                    ["configuration"] = 2f,
                    ["implementation"] = 2f,
                    ["deployment"] = 2f
                }
            };
        }
    }
}