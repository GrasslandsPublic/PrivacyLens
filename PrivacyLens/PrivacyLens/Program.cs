// Program.cs
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using PrivacyLens.Menus;
using PrivacyLens.Services;

// Azure OpenAI v2 SDK + typed clients
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

// For HTTP status & throttling details from the Azure SDK
using System.ClientModel; // ClientResultException

namespace PrivacyLens
{
    internal class Program
    {
        // ---- Behavior toggles (adjust to your preference) ----
        private const bool FailIfDbUnavailable = true;
        private const bool FailIfChunkingAgentUnavailable = true;
        private const bool FailIfEmbeddingsUnavailable = true;
        private const bool CheckDefaultQaAgent = false; // enable if you also want to verify GPT‑5 on startup

        static void Main(string[] args)
        {
            // 1) Build configuration
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // 2) Health checks (DB, Chunking, Embeddings, optional QA)
            if (!RunStartupHealthChecks(config))
            {
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            // 3) Launch UI after successful checks
            var mainMenu = new MainMenu();
            mainMenu.Show();
        }

        private static bool RunStartupHealthChecks(IConfiguration config)
        {
            var ok = true;

            // ---- DB connectivity ----
            try
            {
                var vectorStore = new VectorStore(config);
                var dbOk = vectorStore.CheckDatabaseConnectivityAsync().GetAwaiter().GetResult();
                vectorStore.DisposeAsync().AsTask().GetAwaiter().GetResult();

                if (dbOk) Success("[Startup] Database connection OK.");
                else
                {
                    Error("[Startup] Database connection FAILED. Check ConnectionStrings and server status.");
                    if (FailIfDbUnavailable) return false;
                    ok = false;
                }
            }
            catch (Exception ex)
            {
                Error("[Startup] Database connectivity check threw an exception:");
                Console.WriteLine(ex.Message);
                if (FailIfDbUnavailable) return false;
                ok = false;
            }

            // ---- Azure OpenAI: ChunkingAgent (GPT‑4.1‑mini) ----
            try
            {
                var (endpoint, depName) = GetChunkingAgentConfig(config);
                Info($"[Startup] Checking ChunkingAgent. endpoint={endpoint}, deployment={depName}");

                var result = CheckChunkingAgentAsync(config).GetAwaiter().GetResult();
                if (result) Success("[Startup] ChunkingAgent OK.");
                else
                {
                    Error("[Startup] ChunkingAgent FAILED.");
                    if (FailIfChunkingAgentUnavailable) return false;
                    ok = false;
                }
            }
            catch (ClientResultException cre)
            {
                var (status, msg) = DescribeHttp(cre);
                Error($"[Startup] ChunkingAgent FAILED: HTTP {status}");
                Console.WriteLine(msg);
                WriteChunkingAgentHints(config, status);
                if (FailIfChunkingAgentUnavailable) return false;
                ok = false;
            }
            catch (Exception ex)
            {
                Error("[Startup] ChunkingAgent check threw an exception:");
                Console.WriteLine(ex.Message);
                if (FailIfChunkingAgentUnavailable) return false;
                ok = false;
            }

            // ---- Azure OpenAI: EmbeddingsAgent (preferred) or top‑level embeddings ----
            try
            {
                var (endpoint, depName) = GetEmbeddingConfig(config);
                Info($"[Startup] Checking Embeddings. endpoint={endpoint}, deployment={depName}");

                var result = CheckEmbeddingsAsync(config).GetAwaiter().GetResult();
                if (result) Success("[Startup] Embeddings OK.");
                else
                {
                    Error("[Startup] Embeddings FAILED.");
                    if (FailIfEmbeddingsUnavailable) return false;
                    ok = false;
                }
            }
            catch (ClientResultException cre)
            {
                var (status, msg) = DescribeHttp(cre);
                Error($"[Startup] Embeddings FAILED: HTTP {status}");
                Console.WriteLine(msg);
                WriteEmbeddingsHints(config, status);
                if (FailIfEmbeddingsUnavailable) return false;
                ok = false;
            }
            catch (Exception ex)
            {
                Error("[Startup] Embeddings check threw an exception:");
                Console.WriteLine(ex.Message);
                if (FailIfEmbeddingsUnavailable) return false;
                ok = false;
            }

            // ---- (Optional) Default QA ChatDeployment (e.g., GPT‑5 on shared resource) ----
            if (CheckDefaultQaAgent)
            {
                try
                {
                    var (endpoint, depName) = GetDefaultQaConfig(config);
                    Info($"[Startup] Checking Default QA Agent. endpoint={endpoint}, deployment={depName}");

                    var result = CheckDefaultQaAgentAsync(config).GetAwaiter().GetResult();
                    if (result) Success("[Startup] Default QA Agent OK.");
                    else Warning("[Startup] Default QA Agent FAILED (not required for chunking).");
                }
                catch (ClientResultException cre)
                {
                    var (status, msg) = DescribeHttp(cre);
                    Warning($"[Startup] Default QA Agent FAILED: HTTP {status}");
                    Console.WriteLine(msg);
                    WriteDefaultQaHints(config, status);
                }
                catch (Exception ex)
                {
                    Warning("[Startup] Default QA Agent check threw an exception:");
                    Console.WriteLine(ex.Message);
                }
            }

            return ok;
        }

        // ---------- Azure OpenAI checks ----------

        private static async System.Threading.Tasks.Task<bool> CheckChunkingAgentAsync(IConfiguration config)
        {
            var (endpoint, apiKey, chatDep) = GetChunkingTriple(config);

            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            var chat = client.GetChatClient(chatDep);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a health probe. Respond with 'pong'."),
                new UserChatMessage("ping")
            };

            // Keep it minimal; avoid extra options to reduce risk of unsupported params
            var resp = await chat.CompleteChatAsync(messages);
            var text = resp.Value.Content?[0]?.Text ?? string.Empty;
            return text.Length >= 0; // if we got here, the call succeeded
        }

        private static async System.Threading.Tasks.Task<bool> CheckEmbeddingsAsync(IConfiguration config)
        {
            var (endpoint, apiKey, embDep) = GetEmbeddingTriple(config);

            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            var embed = client.GetEmbeddingClient(embDep);

            var resp = await embed.GenerateEmbeddingsAsync(new[] { "health check" });
            return resp?.Value?.Count > 0 && resp.Value[0].ToFloats().Length > 0;
        }

        private static async System.Threading.Tasks.Task<bool> CheckDefaultQaAgentAsync(IConfiguration config)
        {
            var (endpoint, apiKey, chatDep) = GetDefaultQaTriple(config);

            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            var chat = client.GetChatClient(chatDep);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a health probe. Respond with 'pong'."),
                new UserChatMessage("ping")
            };

            var resp = await chat.CompleteChatAsync(messages);
            var text = resp.Value.Content?[0]?.Text ?? string.Empty;
            return text.Length >= 0;
        }

        // ---------- Config helpers (prefer agent sections when present) ----------

        private static (string endpoint, string dep) GetChunkingAgentConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var agent = aoai.GetSection("ChunkingAgent");
            var endpoint = agent["Endpoint"] ?? aoai["Endpoint"] ?? "<missing>";
            var dep = agent["ChatDeployment"] ?? aoai["ChatDeployment"] ?? "<missing>";
            return (endpoint, dep);
        }

        private static (string endpoint, string dep) GetEmbeddingConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var emb = aoai.GetSection("EmbeddingsAgent");
            var endpoint = emb["Endpoint"] ?? aoai["Endpoint"] ?? "<missing>";
            var dep = emb["EmbeddingDeployment"] ?? aoai["EmbeddingDeployment"] ?? "<missing>";
            return (endpoint, dep);
        }

        private static (string endpoint, string dep) GetDefaultQaConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var endpoint = aoai["Endpoint"] ?? "<missing>";
            var dep = aoai["ChatDeployment"] ?? "<missing>";
            return (endpoint, dep);
        }

        private static (string endpoint, string apiKey, string chatDep) GetChunkingTriple(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var agent = aoai.GetSection("ChunkingAgent");

            var endpoint = agent["Endpoint"] ?? aoai["Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint missing (or ChunkingAgent.Endpoint).");
            var apiKey = agent["ApiKey"] ?? aoai["ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI:ApiKey missing (or ChunkingAgent.ApiKey).");
            var dep = agent["ChatDeployment"] ?? aoai["ChatDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAI:ChatDeployment missing (or ChunkingAgent.ChatDeployment).");

            return (endpoint, apiKey, dep);
        }

        private static (string endpoint, string apiKey, string embDep) GetEmbeddingTriple(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var emb = aoai.GetSection("EmbeddingsAgent");

            var endpoint = emb["Endpoint"] ?? aoai["Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint missing (or EmbeddingsAgent.Endpoint).");
            var apiKey = emb["ApiKey"] ?? aoai["ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI:ApiKey missing (or EmbeddingsAgent.ApiKey).");
            var dep = emb["EmbeddingDeployment"] ?? aoai["EmbeddingDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAI:EmbeddingDeployment missing (or EmbeddingsAgent.EmbeddingDeployment).");

            return (endpoint, apiKey, dep);
        }

        private static (string endpoint, string apiKey, string chatDep) GetDefaultQaTriple(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var endpoint = aoai["Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint missing.");
            var apiKey = aoai["ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI:ApiKey missing.");
            var dep = aoai["ChatDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAI:ChatDeployment missing.");
            return (endpoint, apiKey, dep);
        }

        // ---------- HTTP error helpers ----------

        private static (int status, string message) DescribeHttp(ClientResultException cre)
        {
            int status = 0;
            string detail = cre.Message;

            try
            {
                var resp = cre.GetRawResponse();
                status = resp?.Status ?? 0;
            }
            catch { /* ignored */ }

            return (status, detail);
        }

        private static void WriteChunkingAgentHints(IConfiguration config, int status)
        {
            var (endpoint, dep) = GetChunkingAgentConfig(config);
            if (status == 404)
            {
                Console.WriteLine("Hint: 404 usually means the chat deployment name doesn’t exist at this resource/region.");
                Console.WriteLine($" • Verify a deployment named '{dep}' exists in Azure OpenAI resource at: {endpoint}");
            }
            else if (status == 401)
            {
                Console.WriteLine("Hint: 401 usually means the key doesn’t belong to this resource or is inactive.");
                Console.WriteLine($" • Verify the API key corresponds to resource: {endpoint}");
            }
        }

        private static void WriteEmbeddingsHints(IConfiguration config, int status)
        {
            var (endpoint, dep) = GetEmbeddingConfig(config);
            if (status == 404)
            {
                Console.WriteLine("Hint: 404 means the embedding deployment name doesn’t exist on this resource.");
                Console.WriteLine($" • Create/verify an embedding deployment named '{dep}' in: {endpoint}");
                Console.WriteLine(" • Ensure you’re using the base resource endpoint (no '/openai/deployments/...' suffix).");
            }
            else if (status == 401)
            {
                Console.WriteLine("Hint: 401 indicates invalid API key for this endpoint.");
                Console.WriteLine($" • Re-check the API key for resource: {endpoint}");
            }
        }

        private static void WriteDefaultQaHints(IConfiguration config, int status)
        {
            var (endpoint, dep) = GetDefaultQaConfig(config);
            if (status == 404)
            {
                Console.WriteLine("Hint: 404 means the default ChatDeployment name doesn’t exist at this resource.");
                Console.WriteLine($" • Verify a deployment named '{dep}' exists in resource: {endpoint}");
            }
            else if (status == 401)
            {
                Console.WriteLine("Hint: 401 indicates invalid API key for the shared/default resource.");
                Console.WriteLine($" • Re-check the API key for resource: {endpoint}");
            }
        }

        // ---------- Console color helpers ----------

        private static void Success(string s)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(s);
            Console.ResetColor();
        }

        private static void Warning(string s)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(s);
            Console.ResetColor();
        }

        private static void Error(string s)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(s);
            Console.ResetColor();
        }

        private static void Info(string s)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(s);
            Console.ResetColor();
        }
    }
}
