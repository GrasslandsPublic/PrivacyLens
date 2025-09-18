using System.Collections.Generic;

namespace PrivacyLens.DocumentScoring.Models
{
    public class DocumentFeatures
    {
        public Dictionary<string, int> KeywordFrequencies { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, List<int>> KeywordPositions { get; set; } = new Dictionary<string, List<int>>();
        public float TechnicalTermDensity { get; set; }

        public List<string> SectionHeaders { get; set; } = new List<string>();
        public bool HasTableOfContents { get; set; }
        public bool HasMetadataBlock { get; set; }
        public bool HasNumberedSections { get; set; }
        public int HierarchicalDepth { get; set; }
        public List<string> DetectedPatterns { get; set; } = new List<string>();

        public List<string> ControlIdentifiers { get; set; } = new List<string>();
        public List<string> LegalReferences { get; set; } = new List<string>();
        public List<string> PolicyNumbers { get; set; } = new List<string>();

        public bool HasCodeBlocks { get; set; }
        public bool HasTables { get; set; }
        public bool HasFillableFields { get; set; }
        public bool HasSignatureBlock { get; set; }

        public bool UsesPrescriptiveLanguage { get; set; }
        public float PassiveVoiceRatio { get; set; }
    }
}