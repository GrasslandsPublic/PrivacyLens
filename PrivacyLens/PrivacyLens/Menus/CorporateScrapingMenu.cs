// Menus/CorporateScrapingMenu.cs - Enhanced with smart scrape detection (CORRECTED)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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
        private readonly GovernanceImportPipeline? importPipeline;

        public CorporateScrapingMenu()
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapesPath = Path.Combine(appPath, "governance", "corporate-scrapes");
            scraperService = new WebScraperService(appPath);
            Directory.CreateDirectory(corporateScrapesPath);

            // Build configuration for importer
            config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Initialize import pipeline if configuration is valid
            try
            {
                importPipeline = new GovernanceImportPipeline(
                    new GptChunkingService(config),
                    new EmbeddingService(config),
                    new VectorStore(config),  // Fixed: was PostgresVectorStore
                    config  // Fixed: config comes before logger
                            // Logger is optional, can be null
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not initialize import pipeline: {ex.Message}");
                Console.WriteLine("Import functionality will be unavailable.");
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

            if (importPipeline == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nError: Import pipeline not initialized. Check your configuration.");
                Console.ResetColor();
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Perform the import
            Console.WriteLine("\nStarting import...");

            try
            {
                var progress = new Progress<ImportProgress>(p =>
                {
                    Console.SetCursorPosition(0, Console.CursorTop);
                    // Fixed: Using correct property names from ImportProgress record
                    var info = string.IsNullOrEmpty(p.Info) ? "" : $" - {p.Info}";
                    Console.Write($"[{p.Current}/{p.Total}] {p.File}: {p.Stage}{info}".PadRight(100));
                });

                var task = ImportFilesAsync(scrape, progress);
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

        private async Task ImportFilesAsync(ScrapeInfo scrape, IProgress<ImportProgress> progress)
        {
            if (importPipeline == null)
                throw new InvalidOperationException("Import pipeline not initialized");

            var files = scrape.Files.Select(f => f.FullName).ToArray();
            var total = files.Length;

            // Import files one by one using ImportAsync
            for (int i = 0; i < total; i++)
            {
                await importPipeline.ImportAsync(files[i], progress, i + 1, total);
            }
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
            Console.Write("Use stealth mode for anti-bot protection? (y/N): ");
            var stealthInput = Console.ReadLine();
            bool antiDetection = stealthInput?.Trim().ToLower() == "y";

            Console.WriteLine("\nThis may take several minutes...\n");

            try
            {
                // Fixed: Added all required parameters for ScrapeWebsiteAsync
                var task = scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    antiDetection,
                    maxPages
                );
                var result = task.GetAwaiter().GetResult();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Scraping completed successfully!");
                Console.WriteLine($"  Pages: {result.PagesScraped}");
                Console.WriteLine($"  Documents: {result.DocumentsDownloaded}");
                Console.ResetColor();

                Console.Write("\nWould you like to import the scraped files now? (Y/n): ");
                var importNow = Console.ReadLine()?.Trim().ToLower();

                if (importNow != "n")
                {
                    // Find the most recent scrape (the one we just created)
                    var scrapes = GetExistingScrapes();
                    if (scrapes.Any())
                    {
                        ImportExistingScrape(scrapes.First());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nError during scraping: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewScrapeDetails(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" View Scrape Details");
            Console.WriteLine("========================================");
            Console.WriteLine();

            for (int i = 0; i < scrapes.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {scrapes[i].Name}");
            }

            Console.WriteLine();
            Console.Write("Select scrape to view (or B for back): ");
            var input = Console.ReadLine()?.Trim().ToUpper();

            if (input == "B")
                return;

            if (int.TryParse(input, out int selection) && selection >= 1 && selection <= scrapes.Count)
            {
                var scrape = scrapes[selection - 1];
                Console.Clear();
                Console.WriteLine($"Details for: {scrape.Name}");
                Console.WriteLine("=" + new string('=', scrape.Name.Length + 13));
                Console.WriteLine();
                Console.WriteLine($"Created: {scrape.CreatedDate:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Path: {scrape.Path}");
                Console.WriteLine($"Total Files: {scrape.FileCount}");
                Console.WriteLine($"Total Size: {scrape.TotalSizeMB:F2} MB");
                Console.WriteLine($"Status: {(scrape.IsImported ? "IMPORTED" : "NOT IMPORTED")}");
                Console.WriteLine();
                Console.WriteLine("Files by type:");

                var byExtension = scrape.Files.GroupBy(f => f.Extension.ToLower())
                    .OrderByDescending(g => g.Count());

                foreach (var group in byExtension)
                {
                    Console.WriteLine($"  {group.Key}: {group.Count()} files");

                    // Show first few files as examples
                    foreach (var file in group.Take(3))
                    {
                        var relativePath = Path.GetRelativePath(scrape.Path, file.FullName);
                        Console.WriteLine($"    - {relativePath}");
                    }
                    if (group.Count() > 3)
                        Console.WriteLine($"    ... and {group.Count() - 3} more");
                }
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void DeleteOldScrapes(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Delete Old Scrapes");
            Console.WriteLine("========================================");
            Console.WriteLine();

            var notImported = scrapes.Where(s => !s.IsImported).ToList();
            var imported = scrapes.Where(s => s.IsImported).ToList();

            Console.WriteLine($"Total scrapes: {scrapes.Count}");
            Console.WriteLine($"Imported: {imported.Count}");
            Console.WriteLine($"Not imported: {notImported.Count}");
            Console.WriteLine();

            Console.WriteLine("Options:");
            Console.WriteLine("1. Delete all imported scrapes");
            Console.WriteLine("2. Delete all non-imported scrapes");
            Console.WriteLine("3. Delete specific scrape");
            Console.WriteLine("4. Delete all scrapes");
            Console.WriteLine("B. Back");
            Console.WriteLine();
            Console.Write("Select option: ");

            var input = Console.ReadLine()?.Trim().ToUpper();

            switch (input)
            {
                case "1":
                    DeleteScrapes(imported, "imported");
                    break;
                case "2":
                    DeleteScrapes(notImported, "non-imported");
                    break;
                case "3":
                    DeleteSpecificScrape(scrapes);
                    break;
                case "4":
                    DeleteScrapes(scrapes, "all");
                    break;
                case "B":
                    return;
            }
        }

        private void DeleteScrapes(List<ScrapeInfo> scrapesToDelete, string description)
        {
            if (!scrapesToDelete.Any())
            {
                Console.WriteLine($"\nNo {description} scrapes to delete.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"\nThis will delete {scrapesToDelete.Count} {description} scrape(s).");
            Console.Write("Are you sure? (y/N): ");

            if (Console.ReadLine()?.Trim().ToLower() != "y")
                return;

            foreach (var scrape in scrapesToDelete)
            {
                try
                {
                    Directory.Delete(scrape.Path, true);
                    Console.WriteLine($"Deleted: {scrape.Name}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed to delete {scrape.Name}: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void DeleteSpecificScrape(List<ScrapeInfo> scrapes)
        {
            Console.Clear();
            Console.WriteLine("Select scrape to delete:");
            Console.WriteLine();

            for (int i = 0; i < scrapes.Count; i++)
            {
                var scrape = scrapes[i];
                Console.Write($"{i + 1}. ");
                if (scrape.IsImported)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[IMPORTED] ");
                    Console.ResetColor();
                }
                Console.WriteLine($"{scrape.Name}");
            }

            Console.WriteLine();
            Console.Write("Select scrape number (or B for back): ");
            var input = Console.ReadLine()?.Trim().ToUpper();

            if (input == "B")
                return;

            if (int.TryParse(input, out int selection) && selection >= 1 && selection <= scrapes.Count)
            {
                var scrape = scrapes[selection - 1];
                Console.Write($"\nDelete {scrape.Name}? (y/N): ");

                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    try
                    {
                        Directory.Delete(scrape.Path, true);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Deleted: {scrape.Name}");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Failed to delete: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private bool IsSupportedFile(string extension)
        {
            var supported = new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".html", ".htm", ".csv", ".xls", ".xlsx" };
            return supported.Contains(extension.ToLower());
        }

        // Helper class to store scrape information
        private class ScrapeInfo
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public DateTime CreatedDate { get; set; }
            public int FileCount { get; set; }
            public double TotalSizeMB { get; set; }
            public List<FileInfo> Files { get; set; } = new();
            public bool IsImported { get; set; }
        }
    }
}