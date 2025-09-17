// File: ChunkingStrategies/BaseChunkingStrategy.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using PrivacyLens.DocumentProcessing.Models;  // Add this for ChunkingOptions and other models

namespace PrivacyLens.DocumentProcessing.ChunkingStrategies
{
    public abstract class BaseChunkingStrategy : IChunkingStrategy
    {
        protected readonly ILogger _logger;
        protected readonly Tokenizer _tokenizer;

        public abstract string StrategyName { get; }

        protected BaseChunkingStrategy(ILogger logger)
        {
            _logger = logger;
            _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
        }

        public abstract Task<ChunkingResult> ChunkAsync(
            string content,
            ChunkingOptions options = null,
            IProgress<ProgressUpdate> progress = null,
            CancellationToken cancellationToken = default);

        protected int CountTokens(string text)
        {
            return _tokenizer.CountTokens(text);
        }

        protected ChunkingStatistics CalculateStatistics(List<DocumentChunk> chunks)
        {
            if (!chunks.Any()) return new ChunkingStatistics();

            var tokenCounts = chunks.Select(c => c.TokenCount).ToList();

            return new ChunkingStatistics
            {
                TotalChunks = chunks.Count,
                AverageChunkSize = tokenCounts.Average(),
                MinChunkSize = tokenCounts.Min(),
                MaxChunkSize = tokenCounts.Max(),
                TotalTokens = tokenCounts.Sum()
            };
        }

        protected void ReportProgress(
            IProgress<ProgressUpdate> progress,
            string status,
            double? percentage = null,
            int? chunksCreated = null)
        {
            progress?.Report(new ProgressUpdate
            {
                Phase = "Chunking",
                Status = status,
                Icon = "✂️",
                Percentage = percentage?.GetHashCode(),
                ChunksCreated = chunksCreated
            });
        }
    }
}