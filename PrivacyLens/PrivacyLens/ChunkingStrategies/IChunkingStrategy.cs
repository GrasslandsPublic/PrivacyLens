// File: ChunkingStrategies/IChunkingStrategy.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using PrivacyLens.DocumentProcessing.Models;  // Add this for ChunkingOptions and ChunkingResult

namespace PrivacyLens.DocumentProcessing.ChunkingStrategies
{
    public interface IChunkingStrategy
    {
        string StrategyName { get; }
        Task<ChunkingResult> ChunkAsync(
            string content,
            ChunkingOptions options = null,
            IProgress<ProgressUpdate> progress = null,
            CancellationToken cancellationToken = default);
    }
}