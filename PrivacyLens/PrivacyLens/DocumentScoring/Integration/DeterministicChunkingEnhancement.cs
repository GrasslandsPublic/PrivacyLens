using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Integration
{
    public class DeterministicChunkingEnhancement
    {
        private readonly DocumentScoringEngine _scoringEngine;

        public DeterministicChunkingEnhancement()
        {
            _scoringEngine = new DocumentScoringEngine();
        }

        public async Task<DetectionResult> DetectDocumentTypeWithScoringAsync(
            string content,
            string fileName = null)
        {
            try
            {
                var metadata = new DocumentMetadata
                {
                    FileName = fileName,
                    FileType = System.IO.Path.GetExtension(fileName)
                };

                var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

                if (result.Success && result.Confidence >= 85f)
                {
                    return new DetectionResult
                    {
                        Success = true,
                        DocumentType = result.DocumentType,
                        Confidence = result.Confidence / 100f,
                        Metadata = new Dictionary<string, string>
                        {
                            ["Method"] = "ScoringModel",
                            ["ConfidenceLevel"] = result.ConfidenceLevel.ToString()
                        }
                    };
                }

                return new DetectionResult { Success = false };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Detection error: {ex.Message}");
                return new DetectionResult { Success = false };
            }
        }

        public string GetChunkingStrategyName(string documentType)
        {
            return documentType switch
            {
                "Policy & Legal" => "StructureAware",
                "Technical" => "Technical",
                "Financial" => "TableAware",
                "Forms & Templates" => "FormPreserving",
                "Board Documents" => "Chronological",
                _ => "Recursive"
            };
        }
    }

    public class DetectionResult
    {
        public bool Success { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
    }
}