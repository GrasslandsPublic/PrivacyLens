// File: Models/DetectionConfiguration.cs
using System.Collections.Generic;

namespace PrivacyLens.DocumentProcessing.Models
{
    public class DetectionConfiguration
    {
        public const string SectionName = "PrivacyLens:DetectionPatterns";

        public int MaxScanLength { get; set; } = 10000;
        public double ConfidenceThreshold { get; set; } = 0.95;
        public LegalDetectionConfig Legal { get; set; } = new();
        public PolicyDetectionConfig Policy { get; set; } = new();
        public HtmlDetectionConfig Html { get; set; } = new();
        public MarkdownDetectionConfig Markdown { get; set; } = new();
        public TechnicalDetectionConfig Technical { get; set; } = new();
    }

    public class LegalDetectionConfig
    {
        public string SectionPattern { get; set; }
        public string LegalNumberingPattern { get; set; }
        public string SubsectionPattern { get; set; }
        public List<string> LegalMarkers { get; set; } = new();
        public int MinSectionMatches { get; set; } = 3;
        public int MinNumberingMatches { get; set; } = 5;
    }

    public class PolicyDetectionConfig
    {
        public string HeaderPattern { get; set; }
        public string BulletPointPattern { get; set; }
        public List<string> PolicyMarkers { get; set; } = new();
        public int MinHeaderMatches { get; set; } = 2;
    }

    public class HtmlDetectionConfig
    {
        public int MinHeaderCount { get; set; } = 3;
        public int MinDivWithClass { get; set; } = 5;
        public int? MaxScanLength { get; set; } = 10000;
        public double? MinConfidence { get; set; } = 0.5;
        public Dictionary<string, string> Patterns { get; set; } = new Dictionary<string, string>();
        public string[] FileExtensions { get; set; } = new[] { ".html", ".htm", ".xhtml" };
        public string[] ExcludeExtensions { get; set; } = new[] { ".pdf", ".docx", ".doc", ".txt", ".csv", ".xlsx", ".xls", ".json", ".xml" };
    }

    public class MarkdownDetectionConfig
    {
        public string HeaderPattern { get; set; }
        public int MinHeaderCount { get; set; } = 3;
        public int MinListItems { get; set; } = 3;
    }

    public class TechnicalDetectionConfig
    {
        public List<string> ApiMarkers { get; set; } = new();
        public string CodeBlockPattern { get; set; }
        public string JsonPattern { get; set; }
        public int MinCodeBlocks { get; set; } = 2;
    }
}