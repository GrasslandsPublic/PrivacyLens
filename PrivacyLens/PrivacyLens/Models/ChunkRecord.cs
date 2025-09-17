namespace PrivacyLens.Models
{
    /// <summary>
    /// Represents a single chunk of text with metadata
    /// </summary>
    public record ChunkRecord(int Index, string Content, string? DocumentPath, float[]? Embedding)
    {
        // Backward compatibility property
        public string Text => Content;

        // Backward compatibility constructor for code that doesn't provide embedding
        public ChunkRecord(int index, string content, string? documentPath)
            : this(index, content, documentPath, null)
        {
        }
    }
}