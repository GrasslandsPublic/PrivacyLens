// Services/IVectorStore.cs
using PrivacyLens.Models;

namespace PrivacyLens.Services;

public interface IVectorStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default);
}


