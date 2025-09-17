namespace PrivacyLens.DocumentProcessing.Models
{
    public class DocumentBlueprint
    {
        public string DocumentType { get; set; }
        public double Confidence { get; set; }
        public DocumentStructure Structure { get; set; }
        public ChunkingStrategy ChunkingStrategy { get; set; }
        public List<string> Warnings { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string RecommendedStrategy { get; set; }
    }

    public class DocumentStructure
    {
        public bool HasHierarchy { get; set; }
        public string HierarchyPattern { get; set; }
        public List<string> SectionMarkers { get; set; } = new();
        public int Complexity { get; set; }
        public List<string> SpecialElements { get; set; } = new();
    }

    public class ChunkingStrategy
    {
        public string Method { get; set; }
        public int ChunkSize { get; set; }
        public List<string> PreserveElements { get; set; } = new();
    }
}
