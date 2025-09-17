// Models/ChunkRecord.cs
using System;

namespace PrivacyLens.Models
{
    /// <summary>
    /// Represents a single chunk of text with comprehensive metadata for RAG system
    /// All nullable fields use actual null values, not "NULL" strings
    /// </summary>
    public record ChunkRecord
    {
        // Core fields (required)
        public int Index { get; init; }
        public string Content { get; init; }
        public string DocumentPath { get; init; }
        public float[] Embedding { get; init; }

        // Document metadata (nullable - use null not "NULL")
        public string? DocumentTitle { get; init; }
        public string? DocumentType { get; init; }
        public string? DocumentCategory { get; init; }
        public string? DocumentHash { get; init; }

        // Classification metadata
        public string? DocStructure { get; init; }
        public string? Sensitivity { get; init; }
        public string? ChunkingStrategy { get; init; }

        // Source tracking
        public string? SourceUrl { get; init; }
        public string? SourceSection { get; init; }
        public int? PageNumber { get; init; }

        // Compliance & governance
        public string? Jurisdiction { get; init; }
        public string[]? RegulationRefs { get; init; }
        public string? RiskLevel { get; init; }
        public bool RequiresReview { get; init; }

        // Data governance
        public string[]? DataElements { get; init; }
        public string[]? ThirdParties { get; init; }
        public string? RetentionPeriod { get; init; }

        // Timing & quality
        public DateTime? DocumentDate { get; init; }
        public double? ConfidenceScore { get; init; }
        public int? TokenCount { get; init; }
        public int? OverlapPrevious { get; init; }
        public int? OverlapNext { get; init; }

        // Extensible metadata as JSON
        public Dictionary<string, object>? Metadata { get; init; }

        // Backward compatibility property
        public string Text => Content;

        // Primary constructor with required fields only
        public ChunkRecord(int Index, string Content, string DocumentPath, float[] Embedding)
        {
            this.Index = Index;
            this.Content = Content ?? throw new ArgumentNullException(nameof(Content));
            this.DocumentPath = DocumentPath ?? throw new ArgumentNullException(nameof(DocumentPath));
            this.Embedding = Embedding ?? throw new ArgumentNullException(nameof(Embedding));
        }

        // Constructor with common metadata
        public ChunkRecord(
            int Index,
            string Content,
            string DocumentPath,
            float[] Embedding,
            string? DocumentTitle = null,
            string? DocumentType = null,
            string? SourceUrl = null,
            int? TokenCount = null)
            : this(Index, Content, DocumentPath, Embedding)
        {
            this.DocumentTitle = DocumentTitle;
            this.DocumentType = DocumentType;
            this.SourceUrl = SourceUrl;
            this.TokenCount = TokenCount;
        }

        // Backward compatibility constructor for code that doesn't provide embedding
        public ChunkRecord(int Index, string Content, string? DocumentPath)
            : this(Index, Content, DocumentPath ?? "unknown", Array.Empty<float>())
        {
        }

        // Factory method to create enriched chunk from basic chunk
        public static ChunkRecord CreateEnriched(
            ChunkRecord basicChunk,
            string? documentTitle = null,
            string? documentType = null,
            string? sourceUrl = null,
            int? tokenCount = null,
            Dictionary<string, object>? metadata = null)
        {
            return basicChunk with
            {
                DocumentTitle = documentTitle,
                DocumentType = documentType,
                SourceUrl = sourceUrl,
                TokenCount = tokenCount,
                Metadata = metadata
            };
        }
    }
}