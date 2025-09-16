using System.Collections.Generic;

namespace PrivacyLens.Chunking
{
    public interface ITextChunker
    {
        /// <summary>
        /// Rule-based chunker (no LLM). Returns chunks with small overlap.
        /// </summary>
        IReadOnlyList<string> Chunk(string text, int targetTokens = 500, int overlapTokens = 80);
    }
}

