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
            string endpointStr =
                emb["Endpoint"] ??
                root["Endpoint"] ??
                throw new InvalidOperationException("AzureOpenAI:Endpoint (or EmbeddingsAgent.Endpoint) is missing.");

            string apiKey =
                emb["ApiKey"] ??
                root["ApiKey"] ??
                throw new InvalidOperationException("AzureOpenAI:ApiKey (or EmbeddingsAgent.ApiKey) is missing.");

            string deployment =
                emb["EmbeddingDeployment"] ??
                root["EmbeddingDeployment"] ??
                throw new InvalidOperationException("AzureOpenAI:EmbeddingDeployment (or EmbeddingsAgent.EmbeddingDeployment) is missing.");

            var endpoint = new Uri(endpointStr);
            var client = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));

            _embeddingClient = client.GetEmbeddingClient(deployment);

            // Optional: let you specify dimensions in appsettings if you want (defaults are model-specific).
            EmbeddingDimensions = int.TryParse(root["EmbeddingDimensions"], out var d) ? d : 1536;
            Console.WriteLine($"[Embeddings] endpoint={endpoint}, deployment={deployment}");
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var response = await _embeddingClient.GenerateEmbeddingsAsync(new[] { text }, cancellationToken: ct);
            var mem = response.Value[0].ToFloats();
            return mem.ToArray();
        }
    }
}
