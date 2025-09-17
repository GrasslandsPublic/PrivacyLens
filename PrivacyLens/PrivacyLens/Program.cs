// Program.cs - Fixed version with no unreachable code warnings
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
        // Changed from const to static readonly to avoid unreachable code warnings
        private static readonly bool FailIfDbUnavailable = true;
        private static readonly bool FailIfChunkingAgentUnavailable = true;
        private static readonly bool FailIfEmbeddingsUnavailable = true;
        private static readonly bool CheckDefaultQaAgent = false; // enable if you also want to verify GPT-5 on startup

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
            var allChecksPass = true;

            // ---- DB connectivity ----
            try
            {
                var vectorStore = new VectorStore(config);
                var dbOk = vectorStore.CheckDatabaseConnectivityAsync().GetAwaiter().GetResult();
                vectorStore.DisposeAsync().AsTask().GetAwaiter().GetResult();

                if (dbOk)
                {
                    Success("[Startup] Database connection OK.");
                }
                else
                {
                    Error("[Startup] Database connection FAILED. Check ConnectionStrings and server status.");
                    if (FailIfDbUnavailable)
                    {
                        return false;
                    }
                    else
                    {
                        allChecksPass = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Error("[Startup] Database connectivity check threw an exception:");
                Console.WriteLine(ex.Message);
                if (FailIfDbUnavailable)
                {
                    return false;
                }
                else
                {
                    allChecksPass = false;
                }
            }

            // ---- Azure OpenAI: ChunkingAgent (GPT-4o-mini) ----
            if (!FailIfDbUnavailable || allChecksPass) // Only check if we're continuing
            {
                try
                {
                    var (endpoint, depName) = GetChunkingAgentConfig(config);
                    Info($"[Startup] Checking ChunkingAgent... endpoint={endpoint}, deployment={depName}");

                    var apiKey = GetChunkingAgentApiKey(config);
                    var azClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                    var chat = azClient.GetChatClient(depName);

                    var resp = chat.CompleteChat("hi");
                    Success($"[Startup] ChunkingAgent OK. Model={resp.Value.Model}");
                }
                catch (ClientResultException cre) when (cre.Status == 404 || cre.Status == 401)
                {
                    Error($"[Startup] ChunkingAgent check FAILED (HTTP {cre.Status}).");
                    ShowAzureErrorHints(cre.Status, GetChunkingAgentConfig(config).endpoint, GetChunkingAgentConfig(config).depName);
                    if (FailIfChunkingAgentUnavailable)
                    {
                        return false;
                    }
                    else
                    {
                        allChecksPass = false;
                    }
                }
                catch (Exception ex)
                {
                    Error($"[Startup] ChunkingAgent check threw: {ex.Message}");
                    if (FailIfChunkingAgentUnavailable)
                    {
                        return false;
                    }
                    else
                    {
                        allChecksPass = false;
                    }
                }
            }

            // ---- Embeddings ----
            if ((!FailIfDbUnavailable || allChecksPass) && (!FailIfChunkingAgentUnavailable || allChecksPass))
            {
                try
                {
                    var (endpoint, depName) = GetEmbeddingsConfig(config);
                    Info($"[Startup] Checking Embeddings... endpoint={endpoint}, deployment={depName}");

                    var apiKey = GetEmbeddingsApiKey(config);
                    var azClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                    var emb = azClient.GetEmbeddingClient(depName);

                    var resp = emb.GenerateEmbeddings(new[] { "test" });
                    var dims = resp.Value[0].ToFloats().Length;
                    Success($"[Startup] Embeddings OK. Model={resp.Value.Model}, Dimensions={dims}");
                }
                catch (ClientResultException cre) when (cre.Status == 404 || cre.Status == 401)
                {
                    Error($"[Startup] Embeddings check FAILED (HTTP {cre.Status}).");
                    ShowAzureErrorHints(cre.Status, GetEmbeddingsConfig(config).endpoint, GetEmbeddingsConfig(config).depName);
                    if (FailIfEmbeddingsUnavailable)
                    {
                        return false;
                    }
                    else
                    {
                        allChecksPass = false;
                    }
                }
                catch (Exception ex)
                {
                    Error($"[Startup] Embeddings check threw: {ex.Message}");
                    if (FailIfEmbeddingsUnavailable)
                    {
                        return false;
                    }
                    else
                    {
                        allChecksPass = false;
                    }
                }
            }

            // ---- Optional: DefaultQaAgent (GPT-4o) ----
            if (CheckDefaultQaAgent && allChecksPass)
            {
                try
                {
                    var (endpoint, depName) = GetDefaultQaAgentConfig(config);
                    Info($"[Startup] Checking DefaultQaAgent... endpoint={endpoint}, deployment={depName}");

                    var apiKey = GetDefaultQaAgentApiKey(config);
                    var azClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                    var chat = azClient.GetChatClient(depName);

                    var resp = chat.CompleteChat("hi");
                    Success($"[Startup] DefaultQaAgent OK. Model={resp.Value.Model}");
                }
                catch (Exception ex)
                {
                    Warning($"[Startup] DefaultQaAgent check failed: {ex.Message}");
                    // Not failing the startup for QA agent
                }
            }

            return allChecksPass;
        }

        // ---- Config extractors ----
        private static (string endpoint, string depName) GetChunkingAgentConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var chunkingAgent = aoai.GetSection("ChunkingAgent");

            var endpoint = chunkingAgent["Endpoint"] ?? aoai["Endpoint"] ?? "";
            var depName = chunkingAgent["ChatDeployment"] ?? "gpt-4o-mini";

            return (endpoint, depName);
        }

        private static string GetChunkingAgentApiKey(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var chunkingAgent = aoai.GetSection("ChunkingAgent");
            return chunkingAgent["ApiKey"] ?? aoai["ApiKey"] ?? "";
        }

        private static (string endpoint, string depName) GetEmbeddingsConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var embeddings = aoai.GetSection("EmbeddingsAgent"); // Fixed: EmbeddingsAgent not Embeddings

            // Properly fall back to root endpoint if EmbeddingsAgent doesn't have one
            var endpoint = embeddings["Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = aoai["Endpoint"];
            }
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("No endpoint configured for Embeddings. Check AzureOpenAI:Endpoint or AzureOpenAI:EmbeddingsAgent:Endpoint");
            }

            var depName = embeddings["EmbeddingDeployment"] ?? aoai["EmbeddingDeployment"] ?? "text-embedding-3-large";

            return (endpoint, depName);
        }

        private static string GetEmbeddingsApiKey(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var embeddings = aoai.GetSection("EmbeddingsAgent"); // Fixed: EmbeddingsAgent not Embeddings

            // Properly fall back to root API key if EmbeddingsAgent doesn't have one
            var apiKey = embeddings["ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = aoai["ApiKey"];
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("No API key configured for Embeddings. Check AzureOpenAI:ApiKey or AzureOpenAI:EmbeddingsAgent:ApiKey");
            }

            return apiKey;
        }

        private static (string endpoint, string depName) GetDefaultQaAgentConfig(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var defaultQa = aoai.GetSection("DefaultQaAgent");

            var endpoint = defaultQa["Endpoint"] ?? aoai["Endpoint"] ?? "";
            var depName = defaultQa["ChatDeployment"] ?? "gpt-4o";

            return (endpoint, depName);
        }

        private static string GetDefaultQaAgentApiKey(IConfiguration config)
        {
            var aoai = config.GetSection("AzureOpenAI");
            var defaultQa = aoai.GetSection("DefaultQaAgent");
            return defaultQa["ApiKey"] ?? aoai["ApiKey"] ?? "";
        }

        // ---- Error hints ----
        private static void ShowAzureErrorHints(int httpStatus, string endpoint, string deployment)
        {
            if (httpStatus == 404)
            {
                Console.WriteLine($"  Hints: Check deployment name '{deployment}' exists in Azure OpenAI resource.");
                Console.WriteLine($"         Endpoint: {endpoint}");
            }
            else if (httpStatus == 401)
            {
                Console.WriteLine($"  Hints: Check API key is correct for endpoint {endpoint}");
            }
        }

        // ---- Console helpers ----
        private static void Success(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ {message}");
            Console.ResetColor();
        }

        private static void Error(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {message}");
            Console.ResetColor();
        }

        private static void Warning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ {message}");
            Console.ResetColor();
        }

        private static void Info(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"ℹ {message}");
            Console.ResetColor();
        }
    }
}