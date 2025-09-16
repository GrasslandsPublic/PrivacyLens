// Menus/GovernanceMenu.cs — Streamlined with automatic import from default folder
using Microsoft.Extensions.Configuration;
using Npgsql;
using PrivacyLens.Models;
using PrivacyLens.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PrivacyLens.Menus
{
    public class GovernanceMenu
    {
        private readonly string appPath;
        private readonly CorporateScrapingMenu corporateScrapingMenu;
        private readonly ConfigurationService configService;
        private readonly IConfiguration config;
        private readonly string defaultSourcePath;

        public GovernanceMenu()
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapingMenu = new CorporateScrapingMenu();
            configService = new ConfigurationService();

            // Build configuration
            config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get default source path from configuration
            var paths = configService.GetPaths();
            defaultSourcePath = Path.Combine(appPath, paths.SourceDocuments);

            // Ensure the directory exists
            Directory.CreateDirectory(defaultSourcePath);
        }

        public void Show()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" Governance Database Management");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Import Documents from Default Folder");
                Console.WriteLine("2. Scrape Corporate Website");
                Console.WriteLine("3. View Database Statistics");
                Console.WriteLine("4. Search Documents");
                Console.WriteLine("5. Import from Custom Folder");
                Console.WriteLine("6. Back to Main Menu");
                Console.WriteLine();
                Console.Write("Select option: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        ImportDocumentsFromDefaultFolder();
                        break;

                    case "2":
                        // Launch the corporate scraping submenu
                        corporateScrapingMenu.Show();
                        break;

                    case "3":
                        ViewDatabaseStatistics();
                        break;

                    case "4":
                        SearchDocuments();
                        break;

                    case "5":
                        ImportDocumentsFromCustomFolder();
                        break;

                    case "6":
                        return;

                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void ImportDocumentsFromDefaultFolder()
        {
            Console.Clear();
            Console.WriteLine("Import Documents from Default Folder");
            Console.WriteLine("=====================================");
            Console.WriteLine();
            Console.WriteLine($"Source folder: {defaultSourcePath}");
            Console.WriteLine();

            // Check if folder exists and has files
            if (!Directory.Exists(defaultSourcePath))
            {
                Console.WriteLine("Default folder does not exist. Creating it now...");
                Directory.CreateDirectory(defaultSourcePath);
                Console.WriteLine($"\nPlease place documents in: {defaultSourcePath}");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            // Get all supported files
            var supportedExtensions = configService.GetSupportedFileTypes();
            var files = Directory.GetFiles(defaultSourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            Console.WriteLine($"Found {files.Count} supported document(s):");
            if (files.Count > 0)
            {
                // Show first few files
                var displayCount = Math.Min(5, files.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    Console.WriteLine($"  • {Path.GetFileName(files[i])}");
                }
                if (files.Count > displayCount)
                {
                    Console.WriteLine($"  ... and {files.Count - displayCount} more");
                }
            }
            else
            {
                Console.WriteLine("\nNo documents found in the default folder.");
                Console.WriteLine($"Please place documents in: {defaultSourcePath}");
                Console.WriteLine("\nSupported file types: " + string.Join(", ", supportedExtensions));
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            // Confirm import
            Console.WriteLine();
            Console.Write("Import these documents? (Y/n): ");
            var confirm = Console.ReadLine();
            if (confirm?.Trim().ToLower() == "n")
            {
                return;
            }

            // Import the documents
            Console.WriteLine("\nStarting import...");
            ImportDocumentsAsync(defaultSourcePath, files).GetAwaiter().GetResult();
        }

        private void ImportDocumentsFromCustomFolder()
        {
            Console.Clear();
            Console.WriteLine("Import Documents from Custom Folder");
            Console.WriteLine("====================================");
            Console.WriteLine();
            Console.Write("Enter the folder path containing documents: ");
            var folderPath = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                Console.WriteLine("\nError: Invalid folder path.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Get all supported files
            var supportedExtensions = configService.GetSupportedFileTypes();
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            Console.WriteLine($"\nFound {files.Count} supported document(s).");
            if (files.Count == 0)
            {
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Import the documents
            Console.WriteLine("\nStarting import...");
            ImportDocumentsAsync(folderPath, files).GetAwaiter().GetResult();
        }

        private async Task ImportDocumentsAsync(string sourcePath, List<string> files)
        {
            try
            {
                // Initialize services for import
                var chunker = new GptChunkingService(config);
                var embed = new EmbeddingService(config);
                var store = new VectorStore(config);
                var pipeline = new GovernanceImportPipeline(chunker, embed, store, config);

                int imported = 0;
                int failed = 0;

                Console.WriteLine($"\nProcessing {files.Count} document(s)...\n");

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var fileName = Path.GetFileName(file);

                    try
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[{i + 1}/{files.Count}] Importing: {fileName}");
                        Console.ResetColor();

                        // Create progress reporter
                        var progress = new Progress<ImportProgress>(p =>
                        {
                            if (p.Stage != "Done" && p.Stage != "Error")
                            {
                                Console.WriteLine($"  {p.Stage}: {p.Info ?? ""}");
                            }
                        });

                        await pipeline.ImportAsync(file, progress, i + 1, files.Count);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✅ Successfully imported");
                        Console.ResetColor();
                        imported++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ❌ Failed: {ex.Message}");
                        Console.ResetColor();
                        failed++;
                    }
                }

                // Show summary
                Console.WriteLine("\n========================================");
                Console.WriteLine(" Import Summary");
                Console.WriteLine("========================================");
                Console.WriteLine($" Total files: {files.Count}");
                Console.WriteLine($" ✅ Succeeded: {imported}");
                if (failed > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($" ❌ Failed: {failed}");
                    Console.ResetColor();
                }
                Console.WriteLine("========================================");

                // Dispose of services
                await store.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n❌ Import process failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewDatabaseStatistics()
        {
            Console.Clear();
            Console.WriteLine("Database Statistics");
            Console.WriteLine("===================");
            Console.WriteLine();

            try
            {
                // Get actual stats from database
                var store = new VectorStore(config);
                var task = GetDatabaseStatsAsync(store);
                var stats = task.GetAwaiter().GetResult();

                Console.WriteLine("Governance Database:");
                Console.WriteLine($" Total Chunks: {stats.TotalChunks:N0}");
                Console.WriteLine($" Total Documents: {stats.TotalDocuments:N0}");
                if (stats.TotalChunks > 0)
                {
                    Console.WriteLine($" Average Chunks per Document: {stats.TotalChunks / Math.Max(1, stats.TotalDocuments):N0}");
                }
                Console.WriteLine();

                // Dispose store
                store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting database statistics: {ex.Message}");
            }

            Console.WriteLine("Storage Locations:");
            Console.WriteLine($" Default Source: {defaultSourcePath}");
            Console.WriteLine($" Governance: {Path.Combine(appPath, "governance")}");
            Console.WriteLine($" Corporate Scrapes: {Path.Combine(appPath, "governance", "corporate-scrapes")}");
            Console.WriteLine($" Assessments: {Path.Combine(appPath, "assessments")}");
            Console.WriteLine();

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private async Task<(int TotalChunks, int TotalDocuments)> GetDatabaseStatsAsync(VectorStore store)
        {
            await using var conn = await store.CreateConnectionAsync();

            // Get chunk count
            var chunkCountCmd = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(*) FROM chunks WHERE app_id IS NULL", conn);
            var totalChunks = Convert.ToInt32(await chunkCountCmd.ExecuteScalarAsync() ?? 0);

            // Get document count
            var docCountCmd = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(DISTINCT document_path) FROM chunks WHERE app_id IS NULL", conn);
            var totalDocuments = Convert.ToInt32(await docCountCmd.ExecuteScalarAsync() ?? 0);

            return (totalChunks, totalDocuments);
        }

        private void SearchDocuments()
        {
            Console.Clear();
            Console.WriteLine("Search Documents");
            Console.WriteLine("================");
            Console.WriteLine();
            Console.Write("Enter search query: ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine("\nSearch query cannot be empty.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            try
            {
                Console.WriteLine("\nSearching...\n");

                // Initialize services
                var embed = new EmbeddingService(config);
                var store = new VectorStore(config);

                // Perform search
                var task = SearchDocumentsAsync(query, embed, store);
                var results = task.GetAwaiter().GetResult();

                if (results.Count == 0)
                {
                    Console.WriteLine("No matching documents found.");
                }
                else
                {
                    Console.WriteLine($"Found {results.Count} matching chunk(s):\n");

                    for (int i = 0; i < results.Count; i++)
                    {
                        var result = results[i];
                        Console.WriteLine($"--- Result {i + 1} ---");
                        Console.WriteLine($"Document: {Path.GetFileName(result.DocumentPath)}");
                        Console.WriteLine($"Similarity: {result.Similarity:P2}");

                        // Show preview of content
                        var preview = result.Content.Length > 200
                            ? result.Content.Substring(0, 200) + "..."
                            : result.Content;
                        Console.WriteLine($"Content: {preview}");
                        Console.WriteLine();
                    }
                }

                // Dispose
                store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Search failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task<List<SearchResult>> SearchDocumentsAsync(string query, EmbeddingService embed, VectorStore store)
        {
            // Generate embedding for query
            var queryEmbedding = await embed.EmbedAsync(query);

            // Search for similar chunks (governance only - app_id is NULL)
            var results = await store.SearchAsync(queryEmbedding, topK: 5, appId: "");

            return results;
        }
    }

    // Extension to VectorStore to expose connection creation
    public static class VectorStoreExtensions
    {
        public static async Task<Npgsql.NpgsqlConnection> CreateConnectionAsync(this VectorStore store)
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var connectionString = config.GetConnectionString("PostgresApp")
                ?? throw new InvalidOperationException("Connection string not found");

            var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            var dataSource = dataSourceBuilder.Build();

            return await dataSource.OpenConnectionAsync();
        }
    }
}