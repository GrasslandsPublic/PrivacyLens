using System;
using System.Collections.Generic;

namespace PrivacyLens.Models
{
    // Enhanced document classification based on RAG research Section 6
    public enum DocumentCategory
    {
        Unknown,
        PolicyLegal,        // Policies, procedures, legal documents
        Operational,        // Operational docs, guides, manuals  
        Form,              // Forms, applications, structured documents
        Report,            // Reports with potential tables/data
        Correspondence,    // Emails, letters, communications
        Web,              // Web-scraped content
        Technical         // Technical documentation
    }

    public enum DocumentStructure
    {
        Unknown,
        Hierarchical,      // Has clear sections/subsections
        Tabular,          // Contains significant tables
        Linear,           // Sequential text without clear structure
        Mixed            // Combination of structures
    }

    public enum ContentSensitivity
    {
        Unknown,
        Public,           // Public information
        Internal,         // Internal use only
        Confidential,     // Contains confidential information
        Personal         // Contains personal information (PII)
    }

    public class DocumentInfo
    {
        // Original properties
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public string FileType { get; set; } = string.Empty;

        // New classification properties for RAG chunking strategy selection
        public DocumentCategory Category { get; set; } = DocumentCategory.Unknown;
        public DocumentStructure Structure { get; set; } = DocumentStructure.Unknown;
        public ContentSensitivity Sensitivity { get; set; } = ContentSensitivity.Unknown;

        // Metadata for better routing and processing
        public string DocumentId { get; set; } = Guid.NewGuid().ToString();
        public DateTime DiscoveredAt { get; set; } = DateTime.Now;
        public string DocumentHash { get; set; } = string.Empty; // SHA-256 hash for integrity
        public Dictionary<string, string> AdditionalMetadata { get; set; } = new Dictionary<string, string>();

        // Hints for chunking strategy selection
        public bool LikelyContainsTables { get; set; }
        public bool LikelyContainsPersonalInfo { get; set; }
        public bool RequiresStructurePreservation { get; set; }
        public string DetectedLanguage { get; set; } = "en";

        // For Alberta/Canadian compliance tracking
        public bool ReviewedForPIPA { get; set; } = false;  // Personal Information Protection Act
        public bool ReviewedForFOIP { get; set; } = false;  // Freedom of Information and Protection of Privacy

        // Helper property to get size in readable format
        public string FileSizeFormatted
        {
            get
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double size = FileSizeBytes;

                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }

                return $"{size:0.##} {sizes[order]}";
            }
        }

        // Helper method to determine optimal chunking strategy based on classification
        public string GetRecommendedChunkingStrategy()
        {
            if (Category == DocumentCategory.PolicyLegal && Structure == DocumentStructure.Hierarchical)
                return "StructureAwareRecursive";

            if (Structure == DocumentStructure.Tabular || LikelyContainsTables)
                return "TableAware";

            if (Category == DocumentCategory.Correspondence)
                return "ConversationalContext";

            if (RequiresStructurePreservation)
                return "StructurePreserving";

            return "RecursiveBaseline";
        }
    }

    public class DocumentManifest
    {
        public DateTime CreatedAt { get; set; }
        public string SourceDirectory { get; set; } = string.Empty;
        public List<DocumentInfo> Documents { get; set; } = new List<DocumentInfo>();

        // New classification statistics
        public Dictionary<DocumentCategory, int> CategoryCounts { get; set; } = new Dictionary<DocumentCategory, int>();
        public Dictionary<DocumentStructure, int> StructureCounts { get; set; } = new Dictionary<DocumentStructure, int>();
        public Dictionary<ContentSensitivity, int> SensitivityCounts { get; set; } = new Dictionary<ContentSensitivity, int>();

        public int TotalDocuments => Documents.Count;
        public long TotalSizeBytes => Documents.Sum(d => d.FileSizeBytes);
        public int DocumentsRequiringSpecialHandling => Documents.Count(d => d.LikelyContainsPersonalInfo || d.LikelyContainsTables);

        // Generate classification statistics
        public void UpdateStatistics()
        {
            CategoryCounts.Clear();
            StructureCounts.Clear();
            SensitivityCounts.Clear();

            foreach (var doc in Documents)
            {
                if (!CategoryCounts.ContainsKey(doc.Category))
                    CategoryCounts[doc.Category] = 0;
                CategoryCounts[doc.Category]++;

                if (!StructureCounts.ContainsKey(doc.Structure))
                    StructureCounts[doc.Structure] = 0;
                StructureCounts[doc.Structure]++;

                if (!SensitivityCounts.ContainsKey(doc.Sensitivity))
                    SensitivityCounts[doc.Sensitivity] = 0;
                SensitivityCounts[doc.Sensitivity]++;
            }
        }
    }
}