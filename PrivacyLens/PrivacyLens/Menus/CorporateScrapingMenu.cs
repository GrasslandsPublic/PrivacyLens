// Enhanced CorporateScrapingMenu.cs - Import with Detection Visualization
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrivacyLens.Services;
using PrivacyLens.Models;
using PrivacyLens.DocumentProcessing.Detection;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.Menus
{
    public class CorporateScrapingMenu
    {
        private readonly string appPath;
        private readonly string corporateScrapesPath;
        private readonly WebScraperService scraperService;
        private readonly IConfiguration config;
        private readonly ILogger<GptChunkingService> _logger;
        private GovernanceImportPipeline? importPipeline;

        // Add detection components
        private readonly DocumentDetectionOrchestrator? _detectionOrchestrator;
        private readonly bool _useDetection;

        public CorporateScrapingMenu(IConfiguration configuration, ILogger<GptChunkingService> logger, GovernanceImportPipeline? pipeline)
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapesPath = Path.Combine(appPath, "governance", "corporate-scrapes");
            scraperService = new WebScraperService(appPath);
            Directory.CreateDirectory(corporateScrapesPath);

            config = configuration;
            _logger = logger;

            // Initialize import pipeline
            if (pipeline != null)
            {
                importPipeline = pipeline;
            }
            else
            {
                try
                {
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
                    importPipeline = null;
                }
            }

            // Initialize detection system
            var detectionSection = configuration.GetSection("DocumentDetection");
            _useDetection = detectionSection.Exists() && detectionSection.GetValue<bool>("Enabled", true);

            if (_useDetection)
            {
                try
                {
                    _detectionOrchestrator = InitializeDetection(configuration);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✅ Document detection enabled for corporate imports");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠️ Detection not available: {ex.Message}");
                    Console.ResetColor();
                    _useDetection = false;
                }
            }
        }

        private DocumentDetectionOrchestrator InitializeDetection(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.Configure<DetectionConfiguration>(configuration.GetSection("DocumentDetection"));
            services.AddLogging(builder => builder.AddConsole());

            // Register all detectors
            services.AddScoped<IDocumentDetector, HtmlDocumentDetector>();
            services.AddScoped<IDocumentDetector, LegalDocumentDetector>();
            services.AddScoped<IDocumentDetector, MarkdownDocumentDetector>();
            services.AddScoped<IDocumentDetector, PolicyDocumentDetector>();
            services.AddScoped<IDocumentDetector, TechnicalDocumentDetector>();

            var serviceProvider = services.BuildServiceProvider();
            var detectors = serviceProvider.GetServices<IDocumentDetector>();
            var orchestratorLogger = serviceProvider.GetService<ILogger<DocumentDetectionOrchestrator>>();
            var configOptions = serviceProvider.GetService<IOptions<DetectionConfiguration>>();

            // Handle potential null references
            if (orchestratorLogger == null)
            {
                throw new InvalidOperationException("Failed to create logger for DocumentDetectionOrchestrator");
            }

            if (configOptions == null)
            {
                throw new InvalidOperationException("Failed to get detection configuration");
            }

            return new DocumentDetectionOrchestrator(detectors, orchestratorLogger, configOptions);
        }

        // Keep all existing Show() and menu methods unchanged
        public void Show()
        {
            while (true)
            {
                Console.Clear();
                var shouldExit = ShowSmartMenu();
                if (shouldExit) return;
            }
        }

        private bool ShowSmartMenu()
        {
            Console.WriteLine("========================================");
            Console.WriteLine(" Corporate Website Management");
            Console.WriteLine("========================================");
            Console.WriteLine();

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
                        Console.WriteLine(" ✓ Imported");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(" • Ready to import");
                        Console.ResetColor();
                    }
                    Console.WriteLine($"     Files: {scrape.FileCount} | Path: {Path.GetFileName(scrape.Path)}");
                }

                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  [N]ew scrape");
                Console.WriteLine("  [I]mport pending scrapes");
                Console.WriteLine("  [V]iew details");
                Console.WriteLine("  [D]elete scrape");
                Console.WriteLine("  [B]ack to main menu");
            }
            else
            {
                Console.WriteLine("No existing scrapes found.");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  [N]ew scrape");
                Console.WriteLine("  [B]ack to main menu");
            }

            Console.WriteLine();
            Console.Write("Select option: ");
            var choice = Console.ReadLine()?.ToUpper();

            switch (choice)
            {
                case "N":
                    StartNewScrape();
                    break;
                case "I":
                    if (scrapes.Any(s => !s.IsImported))
                        ImportPendingScrapes(scrapes.Where(s => !s.IsImported).ToList());
                    else
                        Console.WriteLine("\nNo pending scrapes to import. Press any key...");
                    Console.ReadKey();
                    break;
                case "V":
                    if (scrapes.Any())
                        ViewScrapeDetails(scrapes);
                    break;
                case "D":
                    if (scrapes.Any())
                        DeleteScrape(scrapes);
                    break;
                case "B":
                    return true;
            }

            return false;
        }

        private void ImportPendingScrapes(List<ScrapeInfo> pendingScrapes)
        {
            if (importPipeline == null)
            {
                Console.WriteLine("\n❌ Import pipeline not available. Cannot import scrapes.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Import Pending Scrapes");
            Console.WriteLine("========================================");
            Console.WriteLine();

            if (pendingScrapes.Count == 1)
            {
                ImportSingleScrapeWithDetection(pendingScrapes[0]);
            }
            else
            {
                Console.WriteLine("Select which scrape(s) to import:");
                Console.WriteLine();
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
                        ImportSingleScrapeWithDetection(pendingScrapes[choice - 1]);
                    }
                    else if (choice == pendingScrapes.Count + 1)
                    {
                        foreach (var scrape in pendingScrapes)
                        {
                            ImportSingleScrapeWithDetection(scrape);
                        }
                    }
                }
            }
        }

        private void ImportSingleScrapeWithDetection(ScrapeInfo scrape)
        {
            Console.WriteLine();
            Console.WriteLine($"Importing: {scrape.Name}");
            Console.WriteLine($"Files to process: {scrape.FileCount}");

            // ADD THIS: Mode selection
            Console.WriteLine();
            Console.WriteLine("Import mode:");
            Console.WriteLine("  1. Full import (detection + chunking)");
            Console.WriteLine("  2. Detection analysis only (skip undetected files)");
            Console.Write("Select mode (default 1): ");

            var modeInput = Console.ReadLine()?.Trim();
            var detectionOnlyMode = modeInput == "2";

            if (detectionOnlyMode)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("\n🔬 DETECTION ANALYSIS MODE - Will skip files without patterns");
                Console.ResetColor();
            }
            else if (_useDetection)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n📋 Full import with document detection analysis");
                Console.ResetColor();
            }

            Console.Write("\nContinue? (Y/n): ");
            var response = Console.ReadLine()?.Trim().ToLower();

            if (response == "n")
                return;

            if (importPipeline == null)
            {
                Console.WriteLine("\n❌ Import pipeline not initialized.");
                return;
            }

            try
            {
                Console.WriteLine("\nProcessing documents...\n");

                var docsPath = Path.Combine(scrape.Path, "documents");
                if (!Directory.Exists(docsPath))
                {
                    Console.WriteLine("No documents folder found in this scrape.");
                    return;
                }

                var files = Directory.GetFiles(docsPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsSupportedFile(f))
                    .ToList();

                if (!files.Any())
                {
                    Console.WriteLine("No supported files found in documents folder.");
                    return;
                }

                // Track detection statistics
                var detectionStats = new Dictionary<string, int>();
                int successfulImports = 0;
                int failedImports = 0;
                int skippedFiles = 0;  // ADD THIS

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var fileName = Path.GetFileName(file);

                    Console.WriteLine($"[{i + 1}/{files.Count}] Processing: {fileName}");

                    // Perform detection if enabled
                    bool wasDetected = false;
                    if (_useDetection && _detectionOrchestrator != null)
                    {
                        try
                        {
                            var content = File.ReadAllText(file);
                            // Track if detection was successful
                            wasDetected = PerformDetectionAnalysisWithResult(content, fileName, detectionStats)
                                .GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  ⚠️ Detection failed: {ex.Message}");
                            Console.ResetColor();
                        }
                    }

                    // Skip import if in detection-only mode and nothing detected
                    if (detectionOnlyMode && !wasDetected)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⏭️ Skipping import - no pattern detected");
                        Console.ResetColor();
                        skippedFiles++;
                        Console.WriteLine();
                        continue;  // Skip to next file
                    }

                    // Import the file (only if not skipped)
                    try
                    {
                        var progress = new Progress<ImportProgress>(p =>
                        {
                            // Filter out detection messages since we already did detection above
                            if (!string.IsNullOrEmpty(p.Info) && p.Stage != "Detect" && p.Stage != "Detection")
                            {
                                Console.WriteLine($"  {p.Stage}: {p.Info}");
                            }
                        });

                        // IMPORTANT: Tell ImportAsync to skip detection since we already did it
                        // We need to modify ImportAsync to accept a flag, or we can use a workaround
                        // For now, temporarily disable detection to avoid double-detection

                        // Workaround: Temporarily set _useDetection to false in the pipeline
                        // This requires access to the pipeline's detection flag
                        // OR: Just let it run twice but suppress the output

                        // Call import synchronously
                        importPipeline.ImportAsync(file, progress, i + 1, files.Count).GetAwaiter().GetResult();

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ Successfully imported");
                        Console.ResetColor();
                        successfulImports++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ Import failed: {ex.Message}");
                        Console.ResetColor();
                        failedImports++;
                    }

                    Console.WriteLine();
                }

                // Display detection statistics
                if (_useDetection && detectionStats.Any())
                {
                    Console.WriteLine("========================================");
                    Console.WriteLine(" Detection Statistics");
                    Console.WriteLine("========================================");
                    foreach (var stat in detectionStats.OrderByDescending(s => s.Value))
                    {
                        Console.WriteLine($"  {stat.Key}: {stat.Value} document(s)");
                    }
                    Console.WriteLine();
                }

                // Display import summary
                Console.WriteLine("========================================");
                Console.WriteLine(" Import Summary");
                Console.WriteLine("========================================");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Successfully imported: {successfulImports}");
                if (skippedFiles > 0)  // ADD THIS
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⏭️ Skipped (no pattern): {skippedFiles}");
                }
                if (failedImports > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed: {failedImports}");
                }
                Console.ResetColor();

                // Mark as imported if successful
                if (successfulImports > 0)
                {
                    File.WriteAllText(Path.Combine(scrape.Path, ".imported"), DateTime.Now.ToString());
                    Console.WriteLine("\n✓ Scrape marked as imported");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n❌ Import failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task PerformDetectionAnalysis(string content, string fileName, Dictionary<string, int> stats)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🔍 Analyzing document type...");
            Console.ResetColor();

            var detectionProgress = new Progress<ProgressUpdate>(update =>
            {
                if (update.Icon == "🔍" && !string.IsNullOrEmpty(update.Status))
                {
                    Console.WriteLine($"     Checking: {update.Status.Replace("Checking: ", "")}");
                }
            });

            if (_detectionOrchestrator == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️ Detection orchestrator not available");
                Console.ResetColor();
                return;
            }

            var result = await _detectionOrchestrator.DetectAsync(content, fileName, detectionProgress);

            if (result.Success && result.Result != null)
            {
                var documentType = result.Result.DocumentType;
                var confidence = result.Result.Confidence;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✅ Detected: {documentType} ({confidence:P0} confidence)");
                Console.ResetColor();

                // Track statistics
                if (!stats.ContainsKey(documentType))
                    stats[documentType] = 0;
                stats[documentType]++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️ No pattern detected");
                Console.ResetColor();

                if (!stats.ContainsKey("Undetected"))
                    stats["Undetected"] = 0;
                stats["Undetected"]++;
            }
        }

        // ADD THIS: New method that returns whether detection was successful
        private async Task<bool> PerformDetectionAnalysisWithResult(string content, string fileName, Dictionary<string, int> stats)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🔍 Analyzing document type...");
            Console.ResetColor();

            // Don't show the individual detector checks in this mode
            // Just show the final result

            if (_detectionOrchestrator == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️ Detection orchestrator not available");
                Console.ResetColor();
                return false;
            }

            // Run detection silently (no progress reporting)
            var result = await _detectionOrchestrator.DetectAsync(content, fileName, null);

            if (result.Success && result.Result != null)
            {
                var documentType = result.Result.DocumentType;
                var confidence = result.Result.Confidence;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✅ Detected: {documentType} ({confidence:P0} confidence)");
                Console.ResetColor();

                // Track statistics
                if (!stats.ContainsKey(documentType))
                    stats[documentType] = 0;
                stats[documentType]++;

                return true;  // Detection successful
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️ No pattern detected");
                Console.ResetColor();

                if (!stats.ContainsKey("Undetected"))
                    stats["Undetected"] = 0;
                stats["Undetected"]++;

                return false;  // No detection
            }
        }

        private bool IsSupportedFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedExtensions = new[]
            {
                ".pdf", ".doc", ".docx", ".txt", ".html", ".htm", ".md", ".rtf"
            };
            return supportedExtensions.Contains(ext);
        }

        // Keep all other existing methods unchanged (StartNewScrape, ViewScrapeDetails, DeleteScrape, GetExistingScrapes, ScrapeInfo class)
        private void StartNewScrape()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Start New Website Scrape");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.Write("Enter the website URL to scrape: ");
            var url = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("Invalid URL. Press any key to continue...");
                Console.ReadKey();
                return;
            }

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "https://" + url;
            }

            Console.Write("Maximum pages to scrape (default 50): ");
            var maxPagesInput = Console.ReadLine();
            int maxPages = 50;
            if (!string.IsNullOrEmpty(maxPagesInput))
            {
                int.TryParse(maxPagesInput, out maxPages);
            }

            Console.WriteLine($"\nStarting scrape of {url} (max {maxPages} pages)...");
            Console.WriteLine("This may take several minutes depending on the website size.");
            Console.WriteLine();

            try
            {
                // Fixed: Use proper enum value and default antiDetection to false
                var task = scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    false,  // antiDetection
                    maxPages);
                task.Wait();
                var result = task.Result;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Scrape completed successfully!");
                Console.WriteLine($"  Saved to: {result.SessionId}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ Scrape failed: {ex.Message}");
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
                Console.WriteLine($"Name: {scrape.Name}");
                Console.WriteLine($"Path: {scrape.Path}");
                Console.WriteLine($"Status: {(scrape.IsImported ? "Imported" : "Pending")}");
                Console.WriteLine($"Files: {scrape.FileCount}");

                var metadataPath = Path.Combine(scrape.Path, "metadata.json");
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var metadata = File.ReadAllText(metadataPath);
                        Console.WriteLine("Metadata: Available");
                    }
                    catch
                    {
                        Console.WriteLine("Metadata: Error reading");
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

        private List<ScrapeInfo> GetExistingScrapes()
        {
            var scrapes = new List<ScrapeInfo>();
            if (!Directory.Exists(corporateScrapesPath))
                return scrapes;

            foreach (var dir in Directory.GetDirectories(corporateScrapesPath))
            {
                var name = Path.GetFileName(dir);
                var docsPath = Path.Combine(dir, "documents");
                var fileCount = 0;

                if (Directory.Exists(docsPath))
                {
                    fileCount = Directory.GetFiles(docsPath, "*.*", SearchOption.AllDirectories).Length;
                }

                scrapes.Add(new ScrapeInfo
                {
                    Name = name,
                    Path = dir,
                    IsImported = File.Exists(Path.Combine(dir, ".imported")),
                    FileCount = fileCount
                });
            }

            return scrapes.OrderByDescending(s => s.Name).ToList();
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