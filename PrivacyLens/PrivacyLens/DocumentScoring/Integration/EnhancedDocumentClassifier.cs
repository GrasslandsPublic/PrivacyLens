using System;
using System.IO;
using System.Threading.Tasks;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Integration
{
    public class EnhancedDocumentClassifier
    {
        private readonly DocumentScoringEngine _scoringEngine;
        private readonly bool _useScoringModel;
        private readonly float _confidenceThreshold;

        public EnhancedDocumentClassifier(
            bool useScoringModel = true,
            float confidenceThreshold = 85f)
        {
            _useScoringModel = useScoringModel;
            _confidenceThreshold = confidenceThreshold;
            _scoringEngine = new DocumentScoringEngine();
        }

        public async Task<ClassificationResult> ClassifyDocumentAsync(
            string filePath,
            string content = null)
        {
            if (!_useScoringModel)
            {
                return new ClassificationResult { Success = false };
            }

            try
            {
                if (string.IsNullOrEmpty(content))
                {
                    content = await File.ReadAllTextAsync(filePath);
                }

                var metadata = new DocumentMetadata
                {
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    FileType = Path.GetExtension(filePath),
                    Source = "FileSystem"
                };

                var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

                if (result.Success && result.Confidence >= _confidenceThreshold)
                {
                    Console.WriteLine($"[SCORING] Classified with {result.Confidence:F1}% confidence as {result.DocumentType}");

                    return new ClassificationResult
                    {
                        Success = true,
                        DocumentType = result.DocumentType,
                        Confidence = result.Confidence,
                        ChunkingStrategy = GetChunkingStrategy(result.DocumentType)
                    };
                }
                else if (result.Success)
                {
                    Console.WriteLine($"[SCORING] Low confidence ({result.Confidence:F1}%) for {Path.GetFileName(filePath)}");
                }

                return new ClassificationResult { Success = false };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SCORING] Error during scoring: {ex.Message}");
                return new ClassificationResult { Success = false };
            }
        }

        private string GetChunkingStrategy(string documentType)
        {
            return documentType switch
            {
                "Policy & Legal" => "structure-aware",
                "Technical" => "technical-aware",
                "Financial" => "table-aware",
                "Forms & Templates" => "form-preserving",
                "Board Documents" => "chronological",
                "Web Content" => "semantic",
                _ => "recursive"
            };
        }
    }

    public class ClassificationResult
    {
        public bool Success { get; set; }
        public string DocumentType { get; set; }
        public float Confidence { get; set; }
        public string ChunkingStrategy { get; set; }
    }
}