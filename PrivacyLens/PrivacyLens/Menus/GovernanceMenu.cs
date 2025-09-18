using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using PrivacyLens.Models;
using PrivacyLens.Services;
using System;
using System.Collections.Generic;
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
        private readonly ILogger<GptChunkingService> _logger;

        public GovernanceMenu()
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            configService = new ConfigurationService();

            // Build configuration
            config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Create logger factory and logger
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<GptChunkingService>();

            // Initialize CorporateScrapingMenu with required parameters
            corporateScrapingMenu = new CorporateScrapingMenu(config, _logger, null);

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
                Console.WriteLine("6. Clear Database");
                Console.WriteLine("7. Back to Main Menu");
                Console.WriteLine();
                Console.Write("Select an option (1-7): ");
                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        ImportFromDefaultFolder();
                        break;

                    case "2":
                        corporateScrapingMenu.Show();
                        break;

                    case "3":
                        ViewStatistics();
                        break;

                    case "4":
                        SearchDocuments();
                        break;

                    case "5":
                        ImportFromCustomFolder();
                        break;

                    case "6":
                        ClearDatabase();
                        break;

                    case "7":
                        return;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void ImportFromDefaultFolder()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Import from Default Folder");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine($"Default folder: {defaultSourcePath}");
            Console.WriteLine();

            // Check if directory has files
            if (!Directory.Exists(defaultSourcePath))
            {
                Console.WriteLine("Default folder does not exist. Creating it now...");
                Directory.CreateDirectory(defaultSourcePath);
                Console.WriteLine("Folder created. Please add documents and try again.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            var supportedExtensions = new[] { ".pdf", ".docx", ".doc", ".txt", ".html", ".md" };
            var files = Directory.GetFiles(defaultSourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            Console.WriteLine($"Found {files.Count} supported document(s).");
            if (files.Count == 0)
            {
                Console.WriteLine("\nNo supported documents found in the default folder.");
                Console.WriteLine("Supported formats: PDF, DOCX, DOC, TXT, HTML, MD");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("\n1. Classify documents only (no database import)");
            Console.WriteLine("2. Full import (classify + store in database)");
            Console.WriteLine("3. Cancel");
            Console.Write("\nSelect option (1-3): ");
            var option = Console.ReadLine()?.Trim();

            if (option == "3") return;

            bool classifyOnly = option == "1";

            Console.WriteLine($"\nStarting {(classifyOnly ? "classification" : "import")}...");
            ImportDocumentsAsync(defaultSourcePath, files, classifyOnly).GetAwaiter().GetResult();
        }

        private void ImportFromCustomFolder()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Import from Custom Folder");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.Write("Enter the folder path: ");
            var folderPath = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                Console.WriteLine("\nInvalid folder path.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            var supportedExtensions = new[] { ".pdf", ".docx", ".doc", ".txt", ".html", ".md" };
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

            Console.WriteLine("\n1. Classify documents only (no database import)");
            Console.WriteLine("2. Full import (classify + store in database)");
            Console.WriteLine("3. Cancel");
            Console.Write("\nSelect option (1-3): ");
            var option = Console.ReadLine()?.Trim();

            if (option == "3") return;

            bool classifyOnly = option == "1";

            Console.WriteLine($"\nStarting {(classifyOnly ? "classification" : "import")}...");
            ImportDocumentsAsync(folderPath, files, classifyOnly).GetAwaiter().GetResult();
        }

        private async Task ImportDocumentsAsync(string sourcePath, List<string> files, bool classifyOnly = false)
        {
            try
            {
                // Initialize services for import
                var chunker = new GptChunkingService(config, _logger);
                var embed = new EmbeddingService(config);
                var store = new VectorStore(config);
                var pipeline = new GovernanceImportPipeline(chunker, embed, store, config);

                int successful = 0;
                int failed = 0;
                var stats = new Dictionary<string, int>();

                Console.WriteLine($"\nProcessing {files.Count} document(s)...\n");

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var fileName = Path.GetFileName(file);

                    try
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[{i + 1}/{files.Count}] Processing: {fileName}");
                        Console.ResetColor();

                        // Use the new simplified pipeline
                        ClassificationResult result;
                        if (classifyOnly)
                        {
                            result = await pipeline.ClassifyDocumentAsync(file, null);
                        }
                        else
                        {
                            // For now, ImportAsync just does classification too
                            // TODO: When full pipeline is implemented, this will do chunking/embedding/storage
                            result = await pipeline.ImportAsync(file, null);
                        }

                        if (result.Success)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"  ✓ {result.DocumentType} ({result.Confidence:F0}% confidence, {result.ExtractedCharacters:N0} chars)");

                            // Show evidence if available
                            if (result.Evidence != null && result.Evidence.Any())
                            {
                                Console.WriteLine($"     Evidence: {string.Join(", ", result.Evidence.Take(2))}");
                            }

                            Console.ResetColor();
                            successful++;

                            // Track statistics by document type
                            if (!stats.ContainsKey(result.DocumentType))
                                stats[result.DocumentType] = 0;
                            stats[result.DocumentType]++;
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"  ✗ Failed: {result.Error}");
                            Console.ResetColor();
                            failed++;

                            if (!stats.ContainsKey("Failed"))
                                stats["Failed"] = 0;
                            stats["Failed"]++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ Error: {ex.Message}");
                        Console.ResetColor();
                        failed++;

                        if (!stats.ContainsKey("Error"))
                            stats["Error"] = 0;
                        stats["Error"]++;
                    }
                }

                // Show summary
                Console.WriteLine("\n========================================");
                Console.WriteLine(" Processing Summary");
                Console.WriteLine("========================================");
                Console.WriteLine($"Total files: {files.Count}");
                Console.WriteLine($"Successful: {successful}");
                Console.WriteLine($"Failed: {failed}");

                if (stats.Any())
                {
                    Console.WriteLine("\nDocument Types Detected:");
                    foreach (var kvp in stats.OrderByDescending(x => x.Value))
                    {
                        Console.WriteLine($"  {kvp.Key}: {kvp.Value} files");
                    }
                }

                if (!classifyOnly)
                {
                    Console.WriteLine("\nNOTE: Full import (chunking/embedding/storage) not yet implemented.");
                    Console.WriteLine("Currently performing classification only for debugging.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nImport process failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewStatistics()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Database Statistics");
            Console.WriteLine("========================================");
            Console.WriteLine();

            try
            {
                var store = new VectorStore(config);

                // For now, just show a placeholder
                // TODO: Implement actual statistics when database is working
                Console.WriteLine("Database statistics will be available once import pipeline is complete.");
                Console.WriteLine();
                Console.WriteLine("Current capabilities:");
                Console.WriteLine("  ✓ Text extraction from PDF, Word, Excel, PowerPoint, HTML, Text");
                Console.WriteLine("  ✓ Document classification (Policy, Financial, Technical, etc.)");
                Console.WriteLine("  ⏳ Chunking strategies (coming soon)");
                Console.WriteLine("  ⏳ Embedding generation (coming soon)");
                Console.WriteLine("  ⏳ Vector storage (coming soon)");

                store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to get statistics: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void SearchDocuments()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Search Documents");
            Console.WriteLine("========================================");
            Console.WriteLine();

            Console.WriteLine("Search functionality will be available once import pipeline is complete.");
            Console.WriteLine();
            Console.Write("Enter search query (or press Enter to cancel): ");
            var query = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            try
            {
                // Placeholder for search
                Console.WriteLine("\nSearch feature coming soon!");
                Console.WriteLine("Will search through imported governance documents using vector similarity.");
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

        private void ClearDatabase()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Clear Database");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("WARNING: This will remove all governance documents from the database!");
            Console.WriteLine();
            Console.Write("Type 'DELETE' to confirm: ");
            var confirm = Console.ReadLine();

            if (confirm == "DELETE")
            {
                try
                {
                    // TODO: Implement actual database clearing when storage is working
                    Console.WriteLine("\nDatabase clearing will be available once import pipeline is complete.");

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Currently in classification-only mode for debugging.");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed to clear database: {ex.Message}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("\nDatabase clear cancelled.");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }
    }
}