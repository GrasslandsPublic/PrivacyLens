// Menus/CorporateScrapingMenu.cs — Streamlined to remove "Chunk and Import" from main menu.
// Adds "Chunk & Import this scrape" under "View Previous Scrapes" per selection.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class CorporateScrapingMenu
    {
        private readonly string appPath;
        private readonly string corporateScrapesPath;
        private readonly WebScraperService scraperService;
        private readonly IConfiguration config;

        public CorporateScrapingMenu()
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapesPath = Path.Combine(appPath, "governance", "corporate-scrapes");
            scraperService = new WebScraperService(appPath);
            Directory.CreateDirectory(corporateScrapesPath);

            // Build configuration for importer when needed
            config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
        }

        public void Show()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" Corporate Website Scraping");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("This module scrapes your corporate website");
                Console.WriteLine("for governance documentation and policies.");
                Console.WriteLine();
                Console.WriteLine("1. Scrape Corporate Website");
                Console.WriteLine("2. View Previous Scrapes (open / chunk & import)");
                Console.WriteLine("3. Delete Old Scrapes");
                Console.WriteLine("4. Back to Governance Menu");
                Console.WriteLine();
                Console.Write("Select option: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        ScrapeCorporateWebsiteAsync().GetAwaiter().GetResult();
                        break;
                    case "2":
                        ViewPreviousScrapes();
                        break;
                    case "3":
                        DeleteOldScrapes();
                        break;
                    case "4":
                        return;
                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private async Task ScrapeCorporateWebsiteAsync()
        {
            Console.Clear();
            Console.WriteLine("Scrape Corporate Website");
            Console.WriteLine("========================");
            Console.WriteLine();

            // Get URL
            Console.Write("Enter your corporate website URL: ");
            var url = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(url))
            {
                Console.WriteLine("\nError: URL cannot be empty.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Validate URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Console.WriteLine("\nError: Invalid URL format. Please include http:// or https://");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            // Configuration options
            Console.WriteLine("\nConfiguration Options:");
            Console.WriteLine("----------------------");

            // Max pages
            Console.Write("Maximum pages to scrape [100]: ");
            var maxPagesInput = Console.ReadLine();
            int maxPages = string.IsNullOrWhiteSpace(maxPagesInput) ? 100 :
                Math.Max(1, int.Parse(maxPagesInput)); // No upper limit

            // Anti-detection mode
            Console.WriteLine("\nDoes your corporate website have anti-bot protection?");
            Console.Write("Use stealth mode? (y/N): ");
            var stealthInput = Console.ReadLine();
            bool antiDetection = stealthInput?.Trim().ToLower() == "y";

            // Summary
            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("Configuration Summary:");
            Console.WriteLine($" URL: {url}");
            Console.WriteLine($" Domain: {uri.Host}");
            Console.WriteLine($" Max Pages: {maxPages}");
            Console.WriteLine($" Mode: {(antiDetection ? "Stealth (slower)" : "Fast")}");
            Console.WriteLine($" Output: /governance/corporate-scrapes/");
            Console.WriteLine(new string('=', 50));
            Console.Write("\nStart scraping? (Y/n): ");
            var confirm = Console.ReadLine();
            if (confirm?.Trim().ToLower() == "n")
            {
                return;
            }

            Console.WriteLine("\nStarting scrape...\n");
            try
            {
                var result = await scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    antiDetection,
                    maxPages
                );

                Console.WriteLine("\n" + new string('=', 50));
                Console.WriteLine("Scraping Complete!");
                Console.WriteLine(new string('=', 50));
                Console.WriteLine($"Session ID: {result.SessionId}");
                Console.WriteLine($"Duration: {(result.EndTime - result.StartTime).TotalMinutes:F1} minutes");
                Console.WriteLine($"Pages Scraped: {result.PagesScraped}");
                Console.WriteLine($"Documents Downloaded: {result.DocumentsDownloaded}");
                if (result.Failed > 0)
                {
                    Console.WriteLine($"Failed Requests: {result.Failed}");
                }
                Console.WriteLine($"\nEvidence Location:\n{result.EvidencePath}");

                Console.WriteLine("\nTip: While we’re developing, go to 'View Previous Scrapes' to chunk & import this scrape.");
                // Future: prompt here to chunk & import immediately.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Error during scraping: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewPreviousScrapes()
        {
            Console.Clear();
            Console.WriteLine("Previous Corporate Scrapes");
            Console.WriteLine("==========================");
            Console.WriteLine();

            if (!Directory.Exists(corporateScrapesPath))
            {
                Console.WriteLine("No previous scrapes found.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            var scrapes = Directory.GetDirectories(corporateScrapesPath, "scrape_*")
                .OrderByDescending(d => new DirectoryInfo(d).CreationTime)
                .ToList();

            if (scrapes.Count == 0)
            {
                Console.WriteLine("No previous scrapes found.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Found {scrapes.Count} scrape(s):\n");
            for (int i = 0; i < scrapes.Count && i < 25; i++)
            {
                var scrape = scrapes[i];
                var dirInfo = new DirectoryInfo(scrape);
                var metadataFile = Path.Combine(scrape, "scrape_metadata.json");

                Console.WriteLine($"{i + 1,2}. {dirInfo.Name}");
                Console.WriteLine($"    Date: {dirInfo.CreationTime}");

                if (File.Exists(metadataFile))
                {
                    try
                    {
                        var json = File.ReadAllText(metadataFile);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("TargetUrl", out var targetUrl))
                            Console.WriteLine($"    URL: {targetUrl.GetString()}");

                        if (root.TryGetProperty("Stats", out var stats))
                        {
                            var pages = stats.GetProperty("Pages").GetInt32();
                            var docs = stats.GetProperty("Documents").GetInt32();
                            Console.WriteLine($"    Content: {pages} pages, {docs} documents");
                        }
                        if (root.TryGetProperty("Duration", out var duration))
                            Console.WriteLine($"    Duration: {duration.GetDouble():F1} seconds");
                    }
                    catch { /* ignore parse errors */ }
                }

                long size = GetDirectorySize(scrape);
                Console.WriteLine($"    Size: {size / 1024.0 / 1024.0:F1} MB\n");
            }

            Console.WriteLine("Actions:");
            Console.WriteLine(" - Enter the number of a scrape for options (open / chunk & import)");
            Console.WriteLine(" - O  : Open scrapes folder in File Explorer");
            Console.WriteLine(" - B  : Back");
            Console.WriteLine();
            Console.Write("Select: ");
            var input = Console.ReadLine()?.Trim();

            if (string.Equals(input, "O", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start("explorer.exe", corporateScrapesPath);
                return;
            }
            if (string.Equals(input, "B", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(input))
            {
                return;
            }

            if (int.TryParse(input, out int pick) && pick >= 1 && pick <= scrapes.Count)
            {
                var selected = scrapes[pick - 1];
                ShowScrapeActions(selected);
            }
            else
            {
                Console.WriteLine("\nInvalid selection. Press any key to continue...");
                Console.ReadKey();
            }
        }

        private void ShowScrapeActions(string scrapeDir)
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("Scrape Options");
                Console.WriteLine("==============");
                Console.WriteLine($"Path: {scrapeDir}\n");

                Console.WriteLine("1. Open this scrape folder");
                Console.WriteLine("2. Chunk & Import this scrape");
                Console.WriteLine("3. Back");
                Console.WriteLine();
                Console.Write("Select option: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        System.Diagnostics.Process.Start("explorer.exe", scrapeDir);
                        break;

                    case "2":
                        RunChunkAndImport(scrapeDir);
                        // After import, return to previous menu
                        return;

                    case "3":
                        return;

                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void RunChunkAndImport(string scrapeDir)
        {
            Console.Clear();
            Console.WriteLine("Chunk & Import");
            Console.WriteLine("==============\n");
            Console.WriteLine($"Selected scrape: {scrapeDir}\n");

            try
            {
                // Wire up services (reusing your existing pipeline pieces)
                var chunker = new GptChunkingService(config);
                var embed = new EmbeddingService(config);
                var store = new VectorStore(config);
                var pipeline = new GovernanceImportPipeline(chunker, embed, store, config);

                // CorporateScrapeImporter uses hybrid HTML chunking + pipeline for documents
                var importer = new CorporateScrapeImporter(pipeline, config);

                importer.ImportScrapeAsync(scrapeDir).GetAwaiter().GetResult();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n✓ Import complete.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ Import failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void DeleteOldScrapes()
        {
            Console.Clear();
            Console.WriteLine("Delete Old Scrapes");
            Console.WriteLine("==================");
            Console.WriteLine();

            var scrapes = Directory.GetDirectories(corporateScrapesPath, "scrape_*")
                .OrderBy(d => new DirectoryInfo(d).CreationTime)
                .ToList();

            if (scrapes.Count == 0)
            {
                Console.WriteLine("No scrapes found to delete.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Total scrapes: {scrapes.Count}");
            long totalSize = scrapes.Sum(GetDirectorySize);
            Console.WriteLine($"Total size: {totalSize / 1024.0 / 1024.0:F1} MB\n");

            Console.WriteLine("Options:");
            Console.WriteLine("1. Delete scrapes older than 7 days");
            Console.WriteLine("2. Delete scrapes older than 30 days");
            Console.WriteLine("3. Keep only the latest 5 scrapes");
            Console.WriteLine("4. Delete all scrapes");
            Console.WriteLine("5. Cancel");
            Console.WriteLine();
            Console.Write("Select option: ");
            var choice = Console.ReadLine();

            var scrapesToDelete = new List<string>();

            switch (choice)
            {
                case "1":
                    var weekAgo = DateTime.Now.AddDays(-7);
                    scrapesToDelete = scrapes.Where(d => new DirectoryInfo(d).CreationTime < weekAgo).ToList();
                    break;
                case "2":
                    var monthAgo = DateTime.Now.AddDays(-30);
                    scrapesToDelete = scrapes.Where(d => new DirectoryInfo(d).CreationTime < monthAgo).ToList();
                    break;
                case "3":
                    if (scrapes.Count > 5)
                        scrapesToDelete = scrapes.Take(scrapes.Count - 5).ToList();
                    break;
                case "4":
                    scrapesToDelete = scrapes;
                    break;
                default:
                    return;
            }

            if (scrapesToDelete.Count == 0)
            {
                Console.WriteLine("\nNo scrapes match the criteria.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"\nThis will delete {scrapesToDelete.Count} scrape(s).");
            Console.Write("Are you sure? (y/N): ");
            var confirm = Console.ReadLine();
            if (confirm?.Trim().ToLower() != "y")
                return;

            int deleted = 0;
            foreach (var scrape in scrapesToDelete)
            {
                try
                {
                    Directory.Delete(scrape, true);
                    Console.WriteLine($"Deleted: {Path.GetFileName(scrape)}");
                    deleted++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete {Path.GetFileName(scrape)}: {ex.Message}");
                }
            }

            Console.WriteLine($"\nDeleted {deleted} scrape(s).");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private long GetDirectorySize(string path)
        {
            try
            {
                return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }
    }
}
