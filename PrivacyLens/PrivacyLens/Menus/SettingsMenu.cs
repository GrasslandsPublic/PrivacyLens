using System;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class SettingsMenu
    {
        private readonly IConfiguration _config;
        private readonly string _connectionString;

        public SettingsMenu()
        {
            _config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            _connectionString = _config.GetConnectionString("PostgresApp")
                ?? throw new InvalidOperationException("PostgresApp connection string not found");
        }

        public void Show()
        {
            bool back = false;

            while (!back)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" Settings");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Initialize Database (enable pgvector, create tables & indexes)");
                Console.WriteLine("2. Clear Database (WARNING: Deletes all chunks!)");
                Console.WriteLine("3. Show Current Configuration");
                Console.WriteLine("B. Back");
                Console.WriteLine();
                Console.Write("Select an option: ");

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

                switch (choice)
                {
                    case "1":
                        RunInitializeDatabase();
                        break;

                    case "2":
                        RunClearDatabase();
                        break;

                    case "3":
                        ShowCurrentConfiguration();
                        break;

                    case "B":
                        back = true;
                        break;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void RunInitializeDatabase()
        {
            Console.Clear();
            Console.WriteLine("Initializing database...\n");

            try
            {
                // Run the async initializer from this sync entry point
                DatabaseBootstrapService.InitializeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Initialization failed: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to return to Settings...");
            Console.ReadKey();
        }

        private void RunClearDatabase()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" ⚠️  CLEAR DATABASE - WARNING ⚠️");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("This will:");
            Console.WriteLine("  • DROP the 'chunks' table completely");
            Console.WriteLine("  • Delete ALL indexed documents and embeddings");
            Console.WriteLine("  • Require re-indexing all documents");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("This action cannot be undone!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Type 'DELETE ALL' to confirm, or press Enter to cancel:");
            Console.Write("> ");

            var confirmation = Console.ReadLine();

            if (confirmation == "DELETE ALL")
            {
                Console.WriteLine("\nClearing database...");

                try
                {
                    ClearDatabaseAsync().GetAwaiter().GetResult();

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Database cleared successfully!");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("Next steps:");
                    Console.WriteLine("1. Verify your embedding model in appsettings.json");
                    Console.WriteLine("   - text-embedding-3-small = 1536 dimensions");
                    Console.WriteLine("   - text-embedding-3-large = 3072 dimensions");
                    Console.WriteLine("2. Run 'Initialize Database' to create fresh tables");
                    Console.WriteLine("3. Re-index your documents");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"❌ Failed to clear database: {ex.Message}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("\nCancelled - database unchanged.");
            }

            Console.WriteLine("\nPress any key to return to Settings...");
            Console.ReadKey();
        }

        private async System.Threading.Tasks.Task ClearDatabaseAsync()
        {
            await using var dataSource = NpgsqlDataSource.Create(_connectionString);
            await using var conn = await dataSource.OpenConnectionAsync();

            // Drop the chunks table if it exists
            var dropTableCmd = new NpgsqlCommand(@"
                DROP TABLE IF EXISTS chunks CASCADE;
                DROP INDEX IF EXISTS chunks_app_id_idx CASCADE;
                DROP INDEX IF EXISTS chunks_embedding_hnsw CASCADE;
                DROP INDEX IF EXISTS chunks_embedding_ivf CASCADE;
                DROP INDEX IF EXISTS chunks_tsv_gin CASCADE;
            ", conn);

            await dropTableCmd.ExecuteNonQueryAsync();

            // Verify it's gone
            var checkCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) 
                FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'chunks'
            ", conn);

            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count > 0)
            {
                throw new Exception("Table still exists after DROP");
            }
        }

        private void ShowCurrentConfiguration()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Current Configuration");
            Console.WriteLine("========================================");
            Console.WriteLine();

            try
            {
                // Get embedding configuration
                var embeddingDeployment = _config["AzureOpenAI:EmbeddingDeployment"] ?? "Not configured";
                var embeddingDimensions = embeddingDeployment.ToLower() switch
                {
                    "text-embedding-3-small" => "1536",
                    "text-embedding-3-large" => "3072",
                    "text-embedding-ada-002" => "1536",
                    _ => "Unknown"
                };

                Console.WriteLine("Embedding Configuration:");
                Console.WriteLine($"  Model: {embeddingDeployment}");
                Console.WriteLine($"  Dimensions: {embeddingDimensions}");
                Console.WriteLine();

                // Get chunking configuration
                var chunkingDeployment = _config["AzureOpenAI:ChunkingAgent:ChatDeployment"]
                    ?? _config["AzureOpenAI:ChatDeployment"]
                    ?? "Not configured";

                Console.WriteLine("Chunking Configuration:");
                Console.WriteLine($"  Model: {chunkingDeployment}");
                Console.WriteLine($"  Target Panel Tokens: {_config["Chunking:TargetPanelTokens"] ?? "Not set"}");
                Console.WriteLine($"  Max Output Tokens: {_config["Chunking:MaxOutputTokens"] ?? "Not set"}");
                Console.WriteLine();

                // Check database status
                Console.WriteLine("Database Status:");
                try
                {
                    CheckDatabaseStatusAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Cannot connect: {ex.Message}");
                }

                // Get file paths
                Console.WriteLine();
                Console.WriteLine("File Paths:");
                Console.WriteLine($"  Source Documents: {_config["PrivacyLens:Paths:SourceDocuments"] ?? "Not set"}");
                Console.WriteLine($"  Web Content: {_config["PrivacyLens:Paths:WebContent"] ?? "Not set"}");
                Console.WriteLine($"  Temp Files: {_config["PrivacyLens:Paths:TempFiles"] ?? "Not set"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading configuration: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to return to Settings...");
            Console.ReadKey();
        }

        private async System.Threading.Tasks.Task CheckDatabaseStatusAsync()
        {
            await using var dataSource = NpgsqlDataSource.Create(_connectionString);
            await using var conn = await dataSource.OpenConnectionAsync();

            // Check if chunks table exists
            var tableExistsCmd = new NpgsqlCommand(@"
                SELECT COUNT(*) 
                FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'chunks'
            ", conn);

            var tableExists = Convert.ToInt32(await tableExistsCmd.ExecuteScalarAsync()) > 0;

            if (tableExists)
            {
                // Get embedding dimension from table
                var dimCmd = new NpgsqlCommand(@"
                    SELECT 
                        data_type,
                        character_maximum_length
                    FROM information_schema.columns 
                    WHERE table_schema = 'public' 
                    AND table_name = 'chunks' 
                    AND column_name = 'embedding'
                ", conn);

                using var reader = await dimCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var dataType = reader.GetString(0);
                    Console.WriteLine($"  ✓ Table 'chunks' exists");

                    // Extract dimensions from the data type (e.g., "vector(1536)")
                    if (dataType.Contains("(") && dataType.Contains(")"))
                    {
                        var start = dataType.IndexOf('(') + 1;
                        var end = dataType.IndexOf(')');
                        var dims = dataType.Substring(start, end - start);
                        Console.WriteLine($"  ✓ Embedding dimensions: {dims}");

                        // Check if it matches config
                        var configuredDims = _config["AzureOpenAI:EmbeddingDeployment"]?.ToLower() switch
                        {
                            "text-embedding-3-small" => "1536",
                            "text-embedding-3-large" => "3072",
                            _ => "Unknown"
                        };

                        if (dims != configuredDims && configuredDims != "Unknown")
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠️  WARNING: Database expects {dims} but config specifies {configuredDims}");
                            Console.WriteLine($"     You may need to clear and reinitialize the database");
                            Console.ResetColor();
                        }
                    }
                }
                reader.Close();

                // Get chunk count
                var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM chunks", conn);
                var chunkCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
                Console.WriteLine($"  ✓ Chunks stored: {chunkCount:N0}");

                // Get unique documents
                var docCountCmd = new NpgsqlCommand("SELECT COUNT(DISTINCT document_path) FROM chunks", conn);
                var docCount = Convert.ToInt64(await docCountCmd.ExecuteScalarAsync());
                Console.WriteLine($"  ✓ Documents indexed: {docCount:N0}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠️  Table 'chunks' does not exist");
                Console.WriteLine("     Run 'Initialize Database' to create it");
                Console.ResetColor();
            }
        }
    }
}