// Services/IChunkingService.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    public interface IChunkingService
    {
        /// <summary>
        /// Chunk a document using GPT. If <paramref name="onStream"/> is provided and streaming is enabled,
        /// the implementation will invoke it with (charactersSoFar, previewTail).
        /// </summary>
        Task<IReadOnlyList<ChunkRecord>> ChunkAsync(
            string text,
            string? documentPath = null,
            Action<int, string>? onStream = null,
            CancellationToken ct = default);
    }
}
