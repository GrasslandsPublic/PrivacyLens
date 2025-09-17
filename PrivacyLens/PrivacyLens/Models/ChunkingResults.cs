// File: Models/ChunkingResults.cs
using System;
using System.Collections.Generic;

namespace PrivacyLens.DocumentProcessing.Models
{
    public class ChunkingResult
    {
        public List<DocumentChunk> Chunks { get; set; } = new();
        public string Strategy { get; set; }
        public ChunkingStatistics Statistics { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public decimal Cost { get; set; }
    }

    public class DocumentChunk
    {
        public int Index { get; set; }
        public string Content { get; set; }
        public int TokenCount { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
        public string SourceSection { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
    }

    public class ChunkingStatistics
    {
        public int TotalChunks { get; set; }
        public double AverageChunkSize { get; set; }
        public int MinChunkSize { get; set; }
        public int MaxChunkSize { get; set; }
        public int TotalTokens { get; set; }
    }
}