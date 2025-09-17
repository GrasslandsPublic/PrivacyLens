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
        private GovernanceImportPipeline? importPipeline; // FIXED: Removed readonly so we can assign it later

        // FIXED: Added proper constructor with required parameters
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
                    Console.Write($"  {i + 1}. ");

                    // Highlight if already imported
                    if (scrape.IsImported)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write("[IMPORTED] ");
                        Console.ResetColor();
                    }

                    Console.WriteLine($"{scrape.Name} ({scrape.FileCount} files, {scrape.TotalSizeMB:F1} MB)");
                    Console.WriteLine($"     Created: {scrape.CreatedDate:yyyy-MM-dd HH:mm}");
                }

                Console.WriteLine();
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("Options:");
                Console.WriteLine("  1-" + scrapes.Count + ". Import existing scrape");
                Console.WriteLine("  N. Create new scrape");
                Console.WriteLine("  V. View scrape details");
                Console.WriteLine("  D. Delete old scrapes");
                Console.WriteLine("  R. Refresh list");
                Console.WriteLine("  B. Back to Governance Menu");
                Console.WriteLine();
                Console.Write("Select option: ");

                var input = Console.ReadLine()?.Trim().ToUpper();

                if (string.IsNullOrEmpty(input))
                    return false;

                // Check if it's a number (import existing)
                if (int.TryParse(input, out int selection) && selection >= 1 && selection <= scrapes.Count)
                {
                    ImportExistingScrape(scrapes[selection - 1]);
                    return false;
                }

                switch (input)
                {
                    case "N":
                        CreateNewScrape();
                        break;
                    case "V":
                        ViewScrapeDetails(scrapes);
                        break;
                    case "D":
                        DeleteOldScrapes(scrapes);
                        break;
                    case "R":
                        // Just return false to refresh
                        break;
                    case "B":
                        return true; // Exit to parent menu
                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
            else
            {
                Console.WriteLine("No existing scrapes found.");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  1. Create new scrape");
                Console.WriteLine("  2. Back to Governance Menu");
                Console.WriteLine();
                Console.Write("Select option: ");

                var input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1":
                        CreateNewScrape();
                        break;
                    case "2":
                        return true; // Exit to parent menu
                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }

            return false; // Don't exit, continue showing menu
        }

        private List<ScrapeInfo> GetExistingScrapes()
        {
            var scrapes = new List<ScrapeInfo>();

            if (!Directory.Exists(corporateScrapesPath))
                return scrapes;

            var directories = Directory.GetDirectories(corporateScrapesPath)
                .OrderByDescending(d => new DirectoryInfo(d).CreationTime);

            foreach (var dir in directories)
            {
                var dirInfo = new DirectoryInfo(dir);
                var files = dirInfo.GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => IsSupportedFile(f.Extension))
                    .ToList();

                if (files.Any())
                {
                    // Check if there's an import marker file
                    var importMarker = Path.Combine(dir, ".imported");
                    var isImported = File.Exists(importMarker);

                    scrapes.Add(new ScrapeInfo
                    {
                        Path = dir,
                        Name = dirInfo.Name,
                        CreatedDate = dirInfo.CreationTime,
                        FileCount = files.Count,
                        TotalSizeMB = files.Sum(f => f.Length) / (1024.0 * 1024.0),
                        Files = files,
                        IsImported = isImported
                    });
                }
            }

            return scrapes;
        }

        private void ImportExistingScrape(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Import Website Scrape");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine($"Scrape: {scrape.Name}");
            Console.WriteLine($"Files: {scrape.FileCount}");
            Console.WriteLine($"Size: {scrape.TotalSizeMB:F1} MB");

            if (scrape.IsImported)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n⚠ This scrape appears to have been imported already.");
                Console.ResetColor();
                Console.Write("Import anyway? (y/N): ");
                var confirm = Console.ReadLine()?.Trim().ToLower();
                if (confirm != "y")
                    return;
            }

            Console.WriteLine();
            Console.WriteLine("File breakdown:");

            var byExtension = scrape.Files.GroupBy(f => f.Extension.ToLower())
                .OrderByDescending(g => g.Count());

            foreach (var group in byExtension)
            {
                Console.WriteLine($"  {group.Key}: {group.Count()} files");
            }

            Console.WriteLine();
            Console.Write("Proceed with import? (Y/n): ");
            var response = Console.ReadLine()?.Trim().ToLower();

            if (response == "n")
                return;

            // FIXED: Initialize pipeline here if it's null
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
                // FIXED: Use CorporateScrapeImporter with correct constructor
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
                Console.WriteLine("No URL provided.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "https://" + url;
            }

            Console.WriteLine($"\nPreparing to scrape: {url}");

            // Get max pages
            Console.Write("Maximum pages to scrape [50]: ");
            var maxPagesInput = Console.ReadLine();
            int maxPages = string.IsNullOrWhiteSpace(maxPagesInput) ? 50 :
                Math.Max(1, int.Parse(maxPagesInput));

            // Anti-detection mode
            Console.Write("Use stealth mode for anti-bot protection? (Y/n): ");
            var stealth = Console.ReadLine()?.Trim().ToLower() != "n";

            // Confirm
            Console.WriteLine("\n----------------------------------------");
            Console.WriteLine("Scrape Configuration:");
            Console.WriteLine($"  URL: {url}");
            Console.WriteLine($"  Max pages: {maxPages}");
            Console.WriteLine($"  Stealth mode: {(stealth ? "Enabled" : "Disabled")}");
            Console.WriteLine("----------------------------------------");
            Console.Write("\nStart scraping? (Y/n): ");

            if (Console.ReadLine()?.Trim().ToLower() == "n")
            {
                Console.WriteLine("Scrape cancelled.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Execute scrape
            Console.WriteLine("\nStarting scrape...");
            try
            {
                // FIXED: Use proper method signature with ScrapeTarget enum
                var task = scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    stealth,
                    maxPages);
                var scrapeResult = task.GetAwaiter().GetResult();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Scrape completed!");
                Console.WriteLine($"Pages scraped: {scrapeResult.PagesScraped}");
                Console.WriteLine($"Documents downloaded: {scrapeResult.DocumentsDownloaded}");
                Console.ResetColor();
                Console.WriteLine($"Files saved to: {scrapeResult.EvidencePath}");

                Console.WriteLine("\nWould you like to import the scraped data now? (Y/n): ");
                if (Console.ReadLine()?.Trim().ToLower() != "n")
                {
                    var scrapeInfo = new ScrapeInfo
                    {
                        Path = scrapeResult.EvidencePath,
                        Name = Path.GetFileName(scrapeResult.EvidencePath),
                        CreatedDate = DateTime.Now,
                        Files = Directory.EnumerateFiles(scrapeResult.EvidencePath, "*.*",
                            new EnumerationOptions { RecurseSubdirectories = true })
                            .Select(f => new FileInfo(f))
                            .Where(f => IsSupportedFile(f.Extension))
                            .ToList()
                    };
                    scrapeInfo.FileCount = scrapeInfo.Files.Count;
                    scrapeInfo.TotalSizeMB = scrapeInfo.Files.Sum(f => f.Length) / (1024.0 * 1024.0);

                    ImportExistingScrape(scrapeInfo);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nError during scrape: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewScrapeDetails(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("View Scrape Details");
            Console.WriteLine("===================");
            Console.WriteLine();

            if (scrapes.Count == 0)
            {
                Console.WriteLine("No scrapes available.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            for (int i = 0; i < scrapes.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {scrapes[i].Name}");
            }

            Console.WriteLine();
            Console.Write("Select scrape to view (1-" + scrapes.Count + "): ");

            if (int.TryParse(Console.ReadLine(), out int selection) &&
                selection >= 1 && selection <= scrapes.Count)
            {
                var scrape = scrapes[selection - 1];
                ShowScrapeDetails(scrape);
            }
            else
            {
                Console.WriteLine("Invalid selection.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
            }
        }

        private void ShowScrapeDetails(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("Scrape Details");
            Console.WriteLine("==============");
            Console.WriteLine();
            Console.WriteLine($"Name: {scrape.Name}");
            Console.WriteLine($"Path: {scrape.Path}");
            Console.WriteLine($"Created: {scrape.CreatedDate:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Imported: {(scrape.IsImported ? "Yes" : "No")}");
            Console.WriteLine($"Total Files: {scrape.FileCount}");
            Console.WriteLine($"Total Size: {scrape.TotalSizeMB:F2} MB");
            Console.WriteLine();

            Console.WriteLine("File breakdown by extension:");
            var byExtension = scrape.Files.GroupBy(f => f.Extension.ToLower())
                .OrderByDescending(g => g.Count());

            foreach (var group in byExtension)
            {
                var totalSize = group.Sum(f => f.Length) / (1024.0 * 1024.0);
                Console.WriteLine($"  {group.Key}: {group.Count()} files ({totalSize:F2} MB)");
            }

            Console.WriteLine();
            Console.WriteLine("Sample files (first 10):");
            foreach (var file in scrape.Files.Take(10))
            {
                Console.WriteLine($"  • {file.Name} ({file.Length / 1024.0:F1} KB)");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void DeleteOldScrapes(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("Delete Old Scrapes");
            Console.WriteLine("==================");
            Console.WriteLine();

            var oldScrapes = scrapes.Where(s => s.CreatedDate < DateTime.Now.AddDays(-30)).ToList();

            if (oldScrapes.Count == 0)
            {
                Console.WriteLine("No scrapes older than 30 days found.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Found {oldScrapes.Count} scrape(s) older than 30 days:");
            Console.WriteLine();

            foreach (var scrape in oldScrapes)
            {
                Console.WriteLine($"  • {scrape.Name} - {scrape.CreatedDate:yyyy-MM-dd} ({scrape.TotalSizeMB:F1} MB)");
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠ Warning: This will permanently delete these scrapes!");
            Console.ResetColor();
            Console.Write("Proceed with deletion? (yes/N): ");

            var confirm = Console.ReadLine()?.Trim().ToLower();
            if (confirm != "yes")
            {
                Console.WriteLine("Deletion cancelled.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            int deleted = 0;
            foreach (var scrape in oldScrapes)
            {
                try
                {
                    Directory.Delete(scrape.Path, recursive: true);
                    deleted++;
                    Console.WriteLine($"  ✓ Deleted: {scrape.Name}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed to delete {scrape.Name}: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Successfully deleted {deleted} scrape(s).");
            Console.ResetColor();

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private bool IsSupportedFile(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;

            // FIXED: Exclude JSON files (they're just metadata)
            var supportedExtensions = new[] {
                ".html", ".htm", ".pdf", ".doc", ".docx",
                ".xls", ".xlsx", ".ppt", ".pptx",
                ".txt", ".csv", ".rtf", ".xml"
            };

            return supportedExtensions.Contains(extension.ToLower());
        }
    }

    // Helper class for scrape information
    public class ScrapeInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
        public int FileCount { get; set; }
        public double TotalSizeMB { get; set; }
        public List<FileInfo> Files { get; set; } = new List<FileInfo>();
        public bool IsImported { get; set; }
    }
}