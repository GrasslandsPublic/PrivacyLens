// File: Models/DetectionResult.cs
using System.Collections.Generic;

namespace PrivacyLens.DocumentProcessing.Models
{
    public class DetectionResult
    {
        public bool CanHandle { get; set; }
        public double Confidence { get; set; }
        public string DocumentType { get; set; } = "";
        public string RecommendedStrategy { get; set; } = "";
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string Reasoning { get; set; } = "";

        public static DetectionResult NoMatch => new()
        {
            CanHandle = false,
            Confidence = 0,
            Reasoning = "No patterns detected"
        };
    }
}
