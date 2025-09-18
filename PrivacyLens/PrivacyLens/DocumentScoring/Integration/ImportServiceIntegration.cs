using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Integration
{
    public static class ImportServiceIntegration
    {
        private static DocumentScoringEngine _scoringEngine;
        private static bool _enableScoring = true;

        static ImportServiceIntegration()
        {
            _scoringEngine = new DocumentScoringEngine();
        }

        public static async Task<ImportEnhancementResult> EnhanceWithScoringAsync(
            string content,
            string fileName)
        {
            if (!_enableScoring || string.IsNullOrWhiteSpace(content))
            {
                return new ImportEnhancementResult { UseDefault = true };
            }

            try
            {
                var metadata = new DocumentMetadata
                {
                    FileName = fileName,
                    FileType = System.IO.Path.GetExtension(fileName),
                    Source = "FileSystem"
                };

                var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

                if (result.Success && result.Confidence >= 85f)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[SCORING] ✓ Detected: {result.DocumentType} ({result.Confidence:F1}% confidence)");

                    if (result.Evidence?.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"  Evidence: {result.Evidence[0].Feature} (+{result.Evidence[0].FinalScore:F1} points)");
                    }
                    Console.ResetColor();

                    return new ImportEnhancementResult
                    {
                        UseDefault = false,
                        DocumentType = result.DocumentType,
                        Confidence = result.Confidence,
                        ChunkingStrategy = GetChunkingStrategyName(result.DocumentType)
                    };
                }
                else if (result.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SCORING] ? Low confidence: {result.DocumentType} ({result.Confidence:F1}%)");
                    Console.WriteLine($"  Falling back to standard processing...");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SCORING] Error: {ex.Message}");
                Console.ResetColor();
            }

            return new ImportEnhancementResult { UseDefault = true };
        }

        private static string GetChunkingStrategyName(string documentType)
        {
            return documentType switch
            {
                "Policy & Legal" => "StructureAware",
                "Technical" => "Technical",
                "Financial" => "TableAware",
                "Forms & Templates" => "FormPreserving",
                _ => "Recursive"
            };
        }

        public static void EnableScoring(bool enable)
        {
            _enableScoring = enable;
        }
    }

    public class ImportEnhancementResult
    {
        public bool UseDefault { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public string ChunkingStrategy { get; set; }
    }
}