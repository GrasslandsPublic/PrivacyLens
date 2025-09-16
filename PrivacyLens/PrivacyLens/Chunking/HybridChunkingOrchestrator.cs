using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PrivacyLens.Models;
using PrivacyLens.Services;

namespace PrivacyLens.Chunking
{
    /// <summary>
    /// For HTML:
    ///   - DOM-segment into sections
    ///   - If section <= threshold tokens: rule-based chunking
    ///   - Else: GPT chunking via existing GptChunkingService
    /// Configured under appsettings.json -> ChunkingPolicy:Html
    /// </summary>
    public sealed class HybridChunkingOrchestrator
    {
        private readonly IContentSegmenter _segmenter;
        private readonly ITextChunker _simple;
        private readonly IChunkingService _gpt;
        private readonly int _simpleThreshold;
        private readonly int _targetTokens;
        private readonly int _overlapTokens;
        private readonly bool _useGptForLarge;

        public HybridChunkingOrchestrator(
            IContentSegmenter segmenter,
            ITextChunker simple,
            IChunkingService gpt,
            IConfiguration config)
        {
            _segmenter = segmenter;
            _simple = simple;
            _gpt = gpt;

            var root = config.GetSection("ChunkingPolicy:Html");
            _simpleThreshold = root.GetValue<int>("SimpleThresholdTokens", 600);
            _targetTokens = root.GetValue<int>("SimpleTargetTokens", 500);
            _overlapTokens = root.GetValue<int>("SimpleOverlapTokens", 80);
            _useGptForLarge = root.GetValue<bool>("UseGptForLargeSections", true);
        }

        public async Task<IReadOnlyList<ChunkRecord>> ChunkHtmlAsync(
            string html, string sourcePathOrUrl, CancellationToken ct = default)
        {
            var sections = _segmenter.Segment(html, sourcePathOrUrl);
            var all = new List<ChunkRecord>();
            int globalIndex = 0;

            foreach (var s in sections)
            {
                IReadOnlyList<string> pieceChunks;

                if (s.ApproxTokens <= _simpleThreshold || !_useGptForLarge)
                {
                    pieceChunks = _simple.Chunk(s.Text, _targetTokens, _overlapTokens);
                }
                else
                {
                    var recs = await _gpt.ChunkAsync(s.Text, sourcePathOrUrl, onStream: null, ct);
                    pieceChunks = recs.Select(r => r.Content).ToArray();
                }

                foreach (var ch in pieceChunks)
                {
                    all.Add(new ChunkRecord(
                        DocumentPath: sourcePathOrUrl,
                        Index: globalIndex++,
                        Content: ch,
                        Embedding: System.Array.Empty<float>()));
                }
            }

            return all;
        }
    }
}
