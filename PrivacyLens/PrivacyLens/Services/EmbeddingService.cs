// Services/EmbeddingService.cs
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Embeddings;

namespace PrivacyLens.Services;

public sealed class EmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;

    public int EmbeddingDimensions { get; }

    public EmbeddingService(IConfiguration config)
    {
        var section = config.GetSection("AzureOpenAI");
        var endpoint = new Uri(section["Endpoint"] ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is missing"));
        var apiKey = section["ApiKey"] ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is missing");
        var deployment = section["EmbeddingDeployment"] ?? throw new InvalidOperationException("AzureOpenAI:EmbeddingDeployment is missing");

        var azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
        _embeddingClient = azureClient.GetEmbeddingClient(deployment); // v2 pattern
        EmbeddingDimensions = int.TryParse(section["EmbeddingDimensions"], out var d) ? d : 1536;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await _embeddingClient.GenerateEmbeddingsAsync(new[] { text }, cancellationToken: ct);
        var mem = response.Value[0].ToFloats(); // ReadOnlyMemory<float>
        return mem.ToArray();
    }
}


