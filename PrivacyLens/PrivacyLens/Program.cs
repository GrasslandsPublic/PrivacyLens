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
        private const bool CheckDefaultQaAgent = false; // enable if you also want to verify GPT-5 on startup

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
                    if (FailIfDbUnavailable) return false;
                    allChecksPass = false;
                }
            }
            catch (Exception ex)
            {
                Error("[Startup] Database connectivity check threw an exception:");
                Console.WriteLine(ex.Message);
                if (FailIfDbUnavailable) return false;
                allChecksPass = false;
            }

            // ---- Azure OpenAI: ChunkingAgent (GPT-4.1-mini) ----
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
                    if (FailIfChunkingAgentUnavailable) return false;
                    allChecksPass = false;
                }
                catch (Exception ex)
                {
                    Error($"[Startup] ChunkingAgent check threw: {ex.Message}");
                    if (FailIfChunkingAgentUnavailable) return false;
                    allChecksPass = false;
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
                    if (FailIfEmbeddingsUnavailable) return false;
                    allChecksPass = false;
                }
                catch (Exception ex)
                {
                    Error($"[Startup] Embeddings check threw: {ex.Message}");
                    if (FailIfEmbeddingsUnavailable) return false;
                    allChecksPass = false;
                }
            }

            // ---- Optional: Default QA Agent (GPT-5) ----
            if (CheckDefaultQaAgent && allChecksPass)
            {
                try
                {
                    var (endpoint, depName) = GetDefaultQaAgentConfig(config);
                    Info($"[Startup] Checking Default QA Agent... endpoint={endpoint}, deployment={depName}");

                    var apiKey = GetDefaultQaApiKey(config);
                    var azClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                    var chat = azClient.GetChatClient(depName);

                    var resp = chat.CompleteChat("hi");
                    Success($"[Startup] Default QA Agent OK. Model={resp.Value.Model}");
                }
                catch (ClientResultException cre) when (cre.Status == 404 || cre.Status == 401)
                {
                    Warning($"[Startup] Default QA Agent check FAILED (HTTP {cre.Status}).");
                    ShowAzureErrorHints(cre.Status, GetDefaultQaAgentConfig(config).endpoint, GetDefaultQaAgentConfig(config).depName);
                }
                catch (Exception ex)
                {
                    Warning($"[Startup] Default QA Agent check threw: {ex.Message}");
                }
            }

            return allChecksPass;
        }

        // ---------- Config extraction helpers ----------

        private static (string endpoint, string depName) GetChunkingAgentConfig(IConfiguration config)
        {
            var root = config.GetSection("AzureOpenAI:ChunkingAgent");
            var endpoint = root["Endpoint"] ?? config["AzureOpenAI:Endpoint"] ?? "";
            var depName = root["ChatDeployment"] ?? "gpt-4o-mini";
            return (endpoint, depName);
        }

        private static string GetChunkingAgentApiKey(IConfiguration config)
        {
            var root = config.GetSection("AzureOpenAI:ChunkingAgent");
            return root["ApiKey"] ?? config["AzureOpenAI:ApiKey"] ?? "";
        }

        private static (string endpoint, string depName) GetEmbeddingsConfig(IConfiguration config)
        {
            var root = config.GetSection("AzureOpenAI:EmbeddingsAgent");
            var endpoint = root["Endpoint"] ?? config["AzureOpenAI:Endpoint"] ?? "";
            var depName = root["EmbeddingDeployment"] ?? config["AzureOpenAI:EmbeddingDeployment"] ?? "";
            return (endpoint, depName);
        }

        private static string GetEmbeddingsApiKey(IConfiguration config)
        {
            var root = config.GetSection("AzureOpenAI:EmbeddingsAgent");
            return root["ApiKey"] ?? config["AzureOpenAI:ApiKey"] ?? "";
        }

        private static (string endpoint, string depName) GetDefaultQaAgentConfig(IConfiguration config)
        {
            var endpoint = config["AzureOpenAI:Endpoint"] ?? "";
            var depName = config["AzureOpenAI:ChatDeployment"] ?? "";
            return (endpoint, depName);
        }

        private static string GetDefaultQaApiKey(IConfiguration config)
        {
            return config["AzureOpenAI:ApiKey"] ?? "";
        }

        private static void ShowAzureErrorHints(int status, string endpoint, string dep)
        {
            if (status == 404)
            {
                Console.WriteLine("Hint: 404 indicates the deployment doesn't exist or endpoint is incorrect.");
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