// Services/EmbeddingService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Embeddings;

namespace PrivacyLens.Services
{
    public sealed class EmbeddingService : IEmbeddingService
    {
        private readonly EmbeddingClient _embeddingClient;
        public int EmbeddingDimensions { get; }

        public EmbeddingService(IConfiguration config)
        {
            // Read top-level first, then override from EmbeddingsAgent if present.
            var root = config.GetSection("AzureOpenAI");
            if (!root.Exists())
                throw new InvalidOperationException("Missing configuration section 'AzureOpenAI'.");

            var emb = root.GetSection("EmbeddingsAgent");

            // Get endpoint from EmbeddingsAgent first, then fall back to root
            string endpointStr =
                emb["Endpoint"] ??
                root["Endpoint"] ??
                throw new InvalidOperationException("AzureOpenAI:EmbeddingsAgent:Endpoint is missing.");

            // Get API key from EmbeddingsAgent first, then fall back to root
            string apiKey =
                emb["ApiKey"] ??
                root["ApiKey"] ??
                throw new InvalidOperationException("AzureOpenAI:EmbeddingsAgent:ApiKey is missing.");

            // Get deployment name from EmbeddingsAgent first, then fall back to root
            string deployment =
                emb["EmbeddingDeployment"] ??
                root["EmbeddingDeployment"] ??
                throw new InvalidOperationException("AzureOpenAI:EmbeddingsAgent:EmbeddingDeployment is missing.");

            var endpoint = new Uri(endpointStr);
            var client = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));

            _embeddingClient = client.GetEmbeddingClient(deployment);

            // Set dimensions based on the deployment model
            EmbeddingDimensions = deployment?.ToLower() switch
            {
                "text-embedding-3-large" => 3072,
                "text-embedding-3-small" => 1536,
                "text-embedding-ada-002" => 1536,
                _ => int.TryParse(emb["EmbeddingDimensions"] ?? root["EmbeddingDimensions"], out var d) ? d : 1536
            };

            Console.WriteLine($"[Embeddings] endpoint={endpoint.Host}, deployment={deployment}, dimensions={EmbeddingDimensions}");
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            try
            {
                var response = await _embeddingClient.GenerateEmbeddingsAsync(new[] { text }, cancellationToken: ct);
                var embedding = response.Value[0];

                // Convert to float array
                var mem = embedding.ToFloats();
                return mem.ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to generate embedding: {ex.Message}", ex);
            }
        }
    }
}