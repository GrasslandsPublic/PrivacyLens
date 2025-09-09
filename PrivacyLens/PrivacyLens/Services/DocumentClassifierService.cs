using System;
using System.IO;
using System.Security.Cryptography;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Simple document classifier - basic automatic classification
    /// Most classification will be done interactively
    /// </summary>
    public class DocumentClassifierService
    {
        public DocumentInfo ClassifyDocument(DocumentInfo document)
        {
            // Calculate document hash for integrity tracking
            if (File.Exists(document.FilePath))
            {
                document.DocumentHash = CalculateFileHash(document.FilePath);
            }

            // Basic filename-based classification
            var fileNameLower = document.FileName.ToLower();

            // Simple keyword matching for category
            if (fileNameLower.Contains("policy") || fileNameLower.Contains("act") || fileNameLower.Contains("regulation"))
            {
                document.Category = DocumentCategory.PolicyLegal;
                document.RequiresStructurePreservation = true;
            }
            else if (fileNameLower.Contains("report"))
            {
                document.Category = DocumentCategory.Report;
            }
            else if (fileNameLower.Contains("form") || fileNameLower.Contains("template"))
            {
                document.Category = DocumentCategory.Form;
            }
            else if (fileNameLower.Contains("manual") || fileNameLower.Contains("guide"))
            {
                document.Category = DocumentCategory.Operational;
            }
            else if (fileNameLower.Contains("notice") || fileNameLower.Contains("fact") || fileNameLower.Contains("sheet"))
            {
                document.Category = DocumentCategory.Correspondence;
            }
            else
            {
                document.Category = DocumentCategory.Unknown;
            }

            // Set basic defaults for structure and sensitivity
            document.Structure = DocumentStructure.Unknown;
            document.Sensitivity = ContentSensitivity.Unknown;

            // Set metadata for chunking strategy
            document.AdditionalMetadata["ChunkingStrategy"] = document.GetRecommendedChunkingStrategy();
            document.AdditionalMetadata["RequiresManualReview"] = "False";

            return document;
        }

        private string CalculateFileHash(string filePath)
        {
            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Could not calculate hash: {ex.Message}");
                return string.Empty;
            }
        }
    }
}