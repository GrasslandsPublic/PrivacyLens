// Services/IEmbeddingService.cs
namespace PrivacyLens.Services;

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    int EmbeddingDimensions { get; }
}


