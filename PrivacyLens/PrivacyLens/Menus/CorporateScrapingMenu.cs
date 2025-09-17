// Menus/CorporateScrapingMenu.cs - Fixed ALL compilation errors
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Services;
using PrivacyLens.Models;

namespace PrivacyLens.Menus
{
    public class CorporateScrapingMenu
    {
        private readonly string appPath;
        private readonly string corporateScrapesPath;
        private readonly WebScraperService scraperService;
        private readonly IConfiguration config;
        private readonly ILogger<GptChunkingService> _logger;
        private GovernanceImportPipeline? importPipeline; // Removed readonly so we can assign it later

        // Added proper constructor with required parameters
        public CorporateScrapingMenu(IConfiguration configuration, ILogger<GptChunkingService> logger, GovernanceImportPipeline? pipeline)
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapesPath = Path.Combine(appPath, "governance", "corporate-scrapes");
            scraperService = new WebScraperService(appPath);
            Directory.CreateDirectory(corporateScrapesPath);

            config = configuration;
            _logger = logger;

            // Initialize import pipeline - always try to create one if not provided
            if (pipeline != null)
            {
                importPipeline = pipeline;
            }
            else
            {
                try
                {
                    // Create a new pipeline with the logger
                    importPipeline = new GovernanceImportPipeline(
                        new GptChunkingService(config, _logger),
                        new EmbeddingService(config),
                        new VectorStore(config),
                        config
                    );
                    Console.WriteLine("Import pipeline initialized successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not initialize import pipeline: {ex.Message}");
                    Console.WriteLine("Import functionality will be unavailable.");
                    importPipeline = null;
                }
            }
        }

        public void Show()
        {
            while (true)
            {
                Console.Clear();
                var shouldExit = ShowSmartMenu();
                if (shouldExit) return;
            }
        }

        private bool ShowSmartMenu() // Returns true if should exit to parent menu
        {
            Console.WriteLine("========================================");
            Console.WriteLine(" Corporate Website Management");
            Console.WriteLine("========================================");
            Console.WriteLine();

            // Check for existing scrapes
            var scrapes = GetExistingScrapes();

            if (scrapes.Any())
            {
                Console.WriteLine($"Found {scrapes.Count} existing scrape(s):");
                Console.WriteLine();

                for (int i = 0; i < scrapes.Count; i++)
                {
                    var scrape = scrapes[i];
                    Console.Write($"  {i + 1}. {scrape.Name}");

                    if (scrape.IsImported)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(" ✓ [Imported]");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($" - {scrape.FileCount} files ready for import");
                        Console.ResetColor();
                    }
                }
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("No existing scrapes found.");
                Console.WriteLine();
            }

            // Dynamic menu options
            Console.WriteLine("Options:");
            Console.WriteLine("========================================");
            Console.WriteLine("  [N] New Website Scrape");

            if (scrapes.Any(s => !s.IsImported))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  [I] Import Pending Scrapes to Vector Store");
                Console.ResetColor();
            }

            if (scrapes.Any())
            {
                Console.WriteLine("  [V] View Scrape Details");
                Console.WriteLine("  [D] Delete a Scrape");
            }

            Console.WriteLine("  [B] Back to Governance Menu");
            Console.WriteLine("========================================");
            Console.Write("\nYour choice: ");

            var choice = Console.ReadLine()?.ToUpper();

            switch (choice)
            {
                case "N":
                    CreateNewScrape();
                    break;
                case "I" when scrapes.Any(s => !s.IsImported):
                    ImportPendingScrapes(scrapes.Where(s => !s.IsImported).ToList());
                    break;
                case "V" when scrapes.Any():
                    ViewScrapeDetails(scrapes);
                    break;
                case "D" when scrapes.Any():
                    DeleteScrape(scrapes);
                    break;
                case "B":
                    return true; // Exit to parent menu
                default:
                    Console.WriteLine("\nInvalid choice. Press any key to continue...");
                    Console.ReadKey();
                    break;
            }

            return false; // Continue in this menu
        }

        private List<ScrapeInfo> GetExistingScrapes()
        {
            var scrapes = new List<ScrapeInfo>();

            if (!Directory.Exists(corporateScrapesPath))
                return scrapes;

            foreach (var dir in Directory.GetDirectories(corporateScrapesPath))
            {
                var name = Path.GetFileName(dir);
                var isImported = File.Exists(Path.Combine(dir, ".imported"));

                // Count files
                var htmlCount = Directory.Exists(Path.Combine(dir, "webpages"))
                    ? Directory.GetFiles(Path.Combine(dir, "webpages"), "*.html").Length
                    : 0;

                var docCount = Directory.Exists(Path.Combine(dir, "documents"))
                    ? Directory.GetFiles(Path.Combine(dir, "documents")).Length
                    : 0;

                scrapes.Add(new ScrapeInfo
                {
                    Name = name,
                    Path = dir,
                    IsImported = isImported,
                    FileCount = htmlCount + docCount
                });
            }

            return scrapes.OrderBy(s => s.Name).ToList();
        }

        private void ImportPendingScrapes(List<ScrapeInfo> pendingScrapes)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Import Pending Scrapes");
            Console.WriteLine("========================================");
            Console.WriteLine();

            if (pendingScrapes.Count == 1)
            {
                ImportSingleScrape(pendingScrapes[0]);
            }
            else
            {
                Console.WriteLine("Select scrape to import:");
                for (int i = 0; i < pendingScrapes.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {pendingScrapes[i].Name} ({pendingScrapes[i].FileCount} files)");
                }
                Console.WriteLine($"  {pendingScrapes.Count + 1}. Import ALL pending scrapes");
                Console.WriteLine();
                Console.Write("Your choice: ");

                if (int.TryParse(Console.ReadLine(), out int choice))
                {
                    if (choice > 0 && choice <= pendingScrapes.Count)
                    {
                        ImportSingleScrape(pendingScrapes[choice - 1]);
                    }
                    else if (choice == pendingScrapes.Count + 1)
                    {
                        foreach (var scrape in pendingScrapes)
                        {
                            ImportSingleScrape(scrape);
                        }
                    }
                }
            }
        }

        private void ImportSingleScrape(ScrapeInfo scrape)
        {
            Console.WriteLine();
            Console.WriteLine($"Importing: {scrape.Name}");
            Console.WriteLine($"Files to process: {scrape.FileCount}");
            Console.Write("Continue? (Y/n): ");
            var response = Console.ReadLine()?.Trim().ToLower();

            if (response == "n")
                return;

            // Initialize pipeline here if it's null
            if (importPipeline == null)
            {
                try
                {
                    Console.WriteLine("\nInitializing import pipeline...");
                    importPipeline = new GovernanceImportPipeline(
                        new GptChunkingService(config, _logger),
                        new EmbeddingService(config),
                        new VectorStore(config),
                        config
                    );
                    Console.WriteLine("Import pipeline initialized successfully.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\nError: Could not initialize import pipeline: {ex.Message}");
                    Console.ResetColor();
                    Console.WriteLine("Press any key to continue...");
                    Console.ReadKey();
                    return;
                }
            }

            // Perform the import
            Console.WriteLine("\nStarting import...");

            try
            {
                // Use CorporateScrapeImporter with correct constructor (pipeline, config)
                var scrapeImporter = new CorporateScrapeImporter(importPipeline, config);
                var task = scrapeImporter.ImportScrapeAsync(scrape.Path);
                task.GetAwaiter().GetResult();

                // Mark as imported
                File.WriteAllText(Path.Combine(scrape.Path, ".imported"),
                    $"Imported on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Successfully imported {scrape.FileCount} files");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nError during import: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void CreateNewScrape()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Create New Website Scrape");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.Write("Enter the website URL to scrape: ");
            var url = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.WriteLine("No URL provided. Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.Write("Maximum number of pages to scrape (default: 50): ");
            var maxPagesInput = Console.ReadLine();
            int maxPages = 50;
            if (!string.IsNullOrWhiteSpace(maxPagesInput))
            {
                int.TryParse(maxPagesInput, out maxPages);
            }

            Console.WriteLine($"\nStarting scrape of {url} (max {maxPages} pages)...");

            try
            {
                // Ask for stealth mode
                Console.Write("Use stealth mode? (Y/n): ");
                var stealth = Console.ReadLine()?.Trim().ToLower() != "n";

                var result = scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    stealth,
                    maxPages).GetAwaiter().GetResult();

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Scrape completed successfully!");
                Console.ResetColor();
                Console.WriteLine($"  Pages scraped: {result.PagesScraped}");
                Console.WriteLine($"  Documents downloaded: {result.DocumentsDownloaded}");
                Console.WriteLine($"  Session ID: {result.SessionId}");
                Console.WriteLine($"  Duration: {(result.EndTime - result.StartTime).TotalMinutes:F1} minutes");

                Console.WriteLine("\nWould you like to import this scrape now? (Y/n): ");
                var importNow = Console.ReadLine()?.Trim().ToLower();

                if (importNow != "n")
                {
                    var scrapePath = Path.Combine(corporateScrapesPath, result.SessionId);
                    ImportSingleScrape(new ScrapeInfo
                    {
                        Name = result.SessionId,
                        Path = scrapePath,
                        IsImported = false,
                        FileCount = result.PagesScraped + result.DocumentsDownloaded
                    });
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ Error during scraping: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewScrapeDetails(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Scrape Details");
            Console.WriteLine("========================================");
            Console.WriteLine();

            foreach (var scrape in scrapes)
            {
                var metadataPath = Path.Combine(scrape.Path, "_metadata", "scrape_summary.json");

                Console.WriteLine($"Scrape: {scrape.Name}");
                Console.WriteLine($"  Status: {(scrape.IsImported ? "Imported" : "Pending")}");
                Console.WriteLine($"  Files: {scrape.FileCount}");

                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var metadata = File.ReadAllText(metadataPath);
                        // You could parse this JSON to show more details
                        Console.WriteLine("  Metadata: Available");
                    }
                    catch
                    {
                        Console.WriteLine("  Metadata: Error reading");
                    }
                }

                Console.WriteLine();
            }

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private void DeleteScrape(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Delete Scrape");
            Console.WriteLine("========================================");
            Console.WriteLine();

            for (int i = 0; i < scrapes.Count; i++)
            {
                Console.WriteLine($"  {i + 1}. {scrapes[i].Name} {(scrapes[i].IsImported ? "[Imported]" : "")}");
            }
            Console.WriteLine();
            Console.Write("Select scrape to delete (0 to cancel): ");

            if (int.TryParse(Console.ReadLine(), out int choice) && choice > 0 && choice <= scrapes.Count)
            {
                var scrape = scrapes[choice - 1];

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARNING: This will permanently delete '{scrape.Name}' and all its files.");
                Console.ResetColor();
                Console.Write("Are you sure? Type 'DELETE' to confirm: ");

                if (Console.ReadLine() == "DELETE")
                {
                    try
                    {
                        Directory.Delete(scrape.Path, true);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n✓ Scrape deleted successfully.");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n✗ Error deleting scrape: {ex.Message}");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.WriteLine("\nDeletion cancelled.");
                }
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private class ScrapeInfo
        {
            public string Name { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public bool IsImported { get; set; }
            public int FileCount { get; set; }
        }
    }
}