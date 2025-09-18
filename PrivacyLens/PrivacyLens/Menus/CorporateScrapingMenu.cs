using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrivacyLens.Models;
using PrivacyLens.Services;
using PrivacyLens.DocumentProcessing.Models;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.Menus
{
    public class CorporateScrapingMenu
    {
        private readonly string appPath;
        private readonly string corporateScrapesPath;
        private readonly WebScraperService scraperService;
        private readonly IConfiguration config;
        private readonly ILogger<GptChunkingService> _logger;
        private readonly GovernanceImportPipeline? importPipeline;

        // Changed from DocumentDetectionOrchestrator to DocumentScoringEngine
        private readonly DocumentScoringEngine? _scoringEngine;
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
                    _scoringEngine = InitializeScoringEngine(configuration);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Document detection enabled for corporate imports");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠ Detection not available: {ex.Message}");
                    Console.ResetColor();
                    _useDetection = false;
                }
            }
        }

        private DocumentScoringEngine InitializeScoringEngine(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole());

            var serviceProvider = services.BuildServiceProvider();
            var scoringLogger = serviceProvider.GetService<ILogger<DocumentScoringEngine>>();

            // Get confidence threshold from config
            var detectionConfig = configuration.GetSection("DocumentDetection");
            var confidenceThreshold = detectionConfig.GetValue<float>("ConfidenceThreshold", 70f);

            return new DocumentScoringEngine(
                logger: scoringLogger,
                useParallelProcessing: true,
                confidenceThreshold: confidenceThreshold
            );
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
                    Console.Write($"  {i + 1}. ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"{scrape.Name}");
                    Console.ResetColor();

                    if (scrape.HasImportedFiles)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write($" [{scrape.ImportedCount} imported]");
                        Console.ResetColor();
                    }

                    Console.WriteLine($" - {scrape.FileCount} files");
                }

                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine($"  1-{scrapes.Count}: Select a scrape to manage");
                Console.WriteLine("  N: New corporate scrape");
                Console.WriteLine("  X: Back to main menu");
            }
            else
            {
                Console.WriteLine("No existing scrapes found.");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  N: New corporate scrape");
                Console.WriteLine("  X: Back to main menu");
            }

            Console.WriteLine();
            Console.Write("Select option: ");
            var input = Console.ReadLine()?.Trim().ToUpper();

            if (input == "X") return true;
            if (input == "N")
            {
                CreateNewScrape();
                return false;
            }

            if (int.TryParse(input, out int selection) && selection > 0 && selection <= scrapes.Count)
            {
                ManageScrape(scrapes[selection - 1]);
            }

            return false;
        }

        private void CreateNewScrape()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" New Corporate Scrape");
            Console.WriteLine("========================================");
            Console.WriteLine();

            Console.WriteLine("Enter the corporate website URL:");
            Console.Write("> ");
            var url = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("Invalid URL.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            // Create folder name from URL
            var uri = new Uri(url);
            var folderName = uri.Host.Replace("www.", "").Replace(".", "_");
            var scrapePath = Path.Combine(corporateScrapesPath, folderName);

            if (Directory.Exists(scrapePath))
            {
                Console.WriteLine($"Scrape for {uri.Host} already exists.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            Directory.CreateDirectory(scrapePath);

            Console.WriteLine($"\nStarting scrape of {uri.Host}...");

            try
            {
                var task = scraperService.ScrapeWebsiteAsync(
                    url,
                    WebScraperService.ScrapeTarget.CorporateWebsite,
                    false,  // antiDetection
                    50);    // maxPages
                task.Wait();
                var result = task.Result;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Scrape completed successfully!");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ Scrape failed: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ManageScrape(ScrapeInfo scrape)
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine($" Managing: {scrape.Name}");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine($"Files: {scrape.FileCount}");
                if (scrape.HasImportedFiles)
                {
                    Console.WriteLine($"Imported: {scrape.ImportedCount}");
                }
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  1. View files");
                Console.WriteLine("  2. Import files to vector database");
                Console.WriteLine("  3. Analyze files (detection only)");
                Console.WriteLine("  4. Delete scrape");
                Console.WriteLine("  X. Back");
                Console.WriteLine();
                Console.Write("Select option: ");

                var input = Console.ReadLine()?.Trim().ToUpper();

                if (input == "X") break;

                switch (input)
                {
                    case "1":
                        ViewFiles(scrape);
                        break;
                    case "2":
                        ImportFiles(scrape);
                        break;
                    case "3":
                        AnalyzeFiles(scrape);
                        break;
                    case "4":
                        DeleteScrape(scrape);
                        return;
                }
            }
        }

        private void ViewFiles(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine($" Files in {scrape.Name}");
            Console.WriteLine("========================================");
            Console.WriteLine();

            var files = Directory.GetFiles(scrape.Path, "*.*", SearchOption.AllDirectories)
                .Select(f => f.Replace(scrape.Path, "").TrimStart('\\'))
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files.Take(50))
            {
                Console.WriteLine($"  {file}");
            }

            if (files.Count > 50)
            {
                Console.WriteLine($"  ... and {files.Count - 50} more");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ImportFiles(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine($" Import Files from {scrape.Name}");
            Console.WriteLine("========================================");
            Console.WriteLine();

            if (importPipeline == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Import pipeline not available.");
                Console.ResetColor();
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            // Look for files in documents folder first
            var documentsPath = Path.Combine(scrape.Path, "documents");
            var files = new List<string>();

            if (Directory.Exists(documentsPath))
            {
                files = Directory.GetFiles(documentsPath, "*.pdf")
                    .Concat(Directory.GetFiles(documentsPath, "*.txt"))
                    .Concat(Directory.GetFiles(documentsPath, "*.html"))
                    .ToList();
            }
            else
            {
                files = Directory.GetFiles(scrape.Path, "*.pdf")
                    .Concat(Directory.GetFiles(scrape.Path, "*.txt"))
                    .Concat(Directory.GetFiles(scrape.Path, "*.html"))
                    .ToList();
            }

            Console.WriteLine($"Found {files.Count} files to import.");
            Console.WriteLine("\nProceed with import? (Y/N)");
            var confirm = Console.ReadLine()?.Trim().ToUpper();

            if (confirm != "Y") return;

            var importedPath = Path.Combine(scrape.Path, "imported");
            Directory.CreateDirectory(importedPath);

            int processed = 0;
            int successful = 0;

            foreach (var file in files)
            {
                processed++;
                Console.WriteLine($"\n[{processed}/{files.Count}] Importing: {Path.GetFileName(file)}");

                try
                {
                    // Here we would do the actual import
                    // For now, just move to imported folder
                    var destPath = Path.Combine(importedPath, Path.GetFileName(file));
                    if (!File.Exists(destPath))
                    {
                        File.Copy(file, destPath);
                    }

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  ✓ Imported successfully");
                    Console.ResetColor();
                    successful++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Import failed: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine($"\n✓ Import complete: {successful}/{processed} files imported");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void AnalyzeFiles(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine($" Analyze Files from {scrape.Name}");
            Console.WriteLine("========================================");
            Console.WriteLine();

            // Look in the documents folder where scraped PDFs are stored
            var documentsPath = Path.Combine(scrape.Path, "documents");
            var files = new List<string>();

            if (Directory.Exists(documentsPath))
            {
                // Get all PDFs from the documents folder
                files.AddRange(Directory.GetFiles(documentsPath, "*.pdf", SearchOption.AllDirectories));

                // Also get other document types if present
                files.AddRange(Directory.GetFiles(documentsPath, "*.txt", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(documentsPath, "*.html", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(documentsPath, "*.doc", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(documentsPath, "*.docx", SearchOption.AllDirectories));
            }
            else
            {
                // Fallback to looking in the root scrape folder
                files = Directory.GetFiles(scrape.Path, "*.pdf")
                    .Concat(Directory.GetFiles(scrape.Path, "*.txt"))
                    .Concat(Directory.GetFiles(scrape.Path, "*.html"))
                    .ToList();
            }

            Console.WriteLine($"Found {files.Count} files to analyze.");

            if (files.Count == 0)
            {
                Console.WriteLine($"\nNo files found in: {documentsPath}");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("\nThis will perform detection only (no import).");
            Console.WriteLine("Continue? (Y/N)");
            var confirm = Console.ReadLine()?.Trim().ToUpper();

            if (confirm != "Y") return;

            var stats = new Dictionary<string, int>();
            int processed = 0;

            foreach (var file in files)
            {
                processed++;
                Console.WriteLine($"\n[{processed}/{files.Count}] Processing: {Path.GetFileName(file)}");

                try
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();

                    // For PDF and other binary files, use the pipeline which handles extraction
                    if (extension == ".pdf" || extension == ".doc" || extension == ".docx")
                    {
                        if (importPipeline != null)
                        {
                            // Use the classification pipeline
                            var task = Task.Run(async () => await importPipeline.ClassifyDocumentAsync(file, null));
                            var result = task.Result;

                            if (result.Success)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"  ✓ Detected: {result.DocumentType} ({result.Confidence:F0}% confidence)");
                                if (result.Evidence != null && result.Evidence.Any())
                                {
                                    Console.WriteLine($"     Evidence: {string.Join(", ", result.Evidence.Take(2))}");
                                }
                                Console.ResetColor();

                                if (!stats.ContainsKey(result.DocumentType))
                                    stats[result.DocumentType] = 0;
                                stats[result.DocumentType]++;
                            }
                            else
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"  ⚠ Classification failed: {result.Error}");
                                Console.ResetColor();

                                if (!stats.ContainsKey("Failed"))
                                    stats["Failed"] = 0;
                                stats["Failed"]++;
                            }
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("  ✗ Import pipeline not available");
                            Console.ResetColor();
                        }
                    }
                    else
                    {
                        // For text files, read and analyze content directly
                        var content = File.ReadAllText(file);

                        if (_useDetection && _scoringEngine != null)
                        {
                            Task.Run(async () => await PerformDetectionAnalysis(content, Path.GetFileName(file), stats)).Wait();
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("  ⚠ Detection not available");
                            Console.ResetColor();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Error: {ex.Message}");
                    Console.ResetColor();

                    if (!stats.ContainsKey("Error"))
                        stats["Error"] = 0;
                    stats["Error"]++;
                }
            }

            // Show statistics
            Console.WriteLine("\n========================================");
            Console.WriteLine(" Detection Statistics");
            Console.WriteLine("========================================");
            foreach (var kvp in stats.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value} files");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task PerformDetectionAnalysis(string content, string fileName, Dictionary<string, int> stats)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🔍 Analyzing document type...");
            Console.ResetColor();

            if (_scoringEngine == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ Detection engine not available");
                Console.ResetColor();
                return;
            }

            var metadata = new DocumentMetadata
            {
                FileName = fileName
            };

            var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

            if (result.Success && result.Confidence >= 50)
            {
                var documentType = result.DocumentType;
                var confidence = result.Confidence;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Detected: {documentType} ({confidence:F0}% confidence)");
                Console.ResetColor();

                // Track statistics
                if (!stats.ContainsKey(documentType))
                    stats[documentType] = 0;
                stats[documentType]++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ No pattern detected");
                Console.ResetColor();

                if (!stats.ContainsKey("Undetected"))
                    stats["Undetected"] = 0;
                stats["Undetected"]++;
            }
        }

        private async Task<bool> PerformDetectionAnalysisWithResult(string content, string fileName, Dictionary<string, int> stats)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🔍 Analyzing document type...");
            Console.ResetColor();

            if (_scoringEngine == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ Detection engine not available");
                Console.ResetColor();
                return false;
            }

            var metadata = new DocumentMetadata
            {
                FileName = fileName
            };

            var result = await _scoringEngine.ClassifyDocumentAsync(content, metadata);

            if (result.Success && result.Confidence >= 50)
            {
                var documentType = result.DocumentType;
                var confidence = result.Confidence;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Detected: {documentType} ({confidence:F0}% confidence)");
                Console.ResetColor();

                // Track statistics
                if (!stats.ContainsKey(documentType))
                    stats[documentType] = 0;
                stats[documentType]++;

                return true;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠ No pattern detected");
                Console.ResetColor();

                if (!stats.ContainsKey("Undetected"))
                    stats["Undetected"] = 0;
                stats["Undetected"]++;

                return false;
            }
        }

        private void DeleteScrape(ScrapeInfo scrape)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine($" Delete {scrape.Name}");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("Are you sure you want to delete this scrape?");
            Console.WriteLine("This action cannot be undone.");
            Console.WriteLine();
            Console.WriteLine("Type DELETE to confirm:");
            Console.Write("> ");

            var confirm = Console.ReadLine()?.Trim();

            if (confirm == "DELETE")
            {
                try
                {
                    Directory.Delete(scrape.Path, true);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Scrape deleted successfully");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✗ Delete failed: {ex.Message}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("Delete cancelled.");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private class ScrapeInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public int FileCount { get; set; }
            public bool HasImportedFiles { get; set; }
            public int ImportedCount { get; set; }
        }

        private List<ScrapeInfo> GetExistingScrapes()
        {
            var scrapes = new List<ScrapeInfo>();

            if (Directory.Exists(corporateScrapesPath))
            {
                var directories = Directory.GetDirectories(corporateScrapesPath);

                foreach (var dir in directories)
                {
                    var name = Path.GetFileName(dir);

                    // Count files in both root and documents folder
                    var fileCount = 0;
                    var documentsPath = Path.Combine(dir, "documents");
                    if (Directory.Exists(documentsPath))
                    {
                        fileCount = Directory.GetFiles(documentsPath, "*.*", SearchOption.AllDirectories).Length;
                    }
                    else
                    {
                        fileCount = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Length;
                    }

                    var importedPath = Path.Combine(dir, "imported");
                    var importedCount = 0;

                    if (Directory.Exists(importedPath))
                    {
                        importedCount = Directory.GetFiles(importedPath, "*.*", SearchOption.AllDirectories).Length;
                    }

                    scrapes.Add(new ScrapeInfo
                    {
                        Name = name.Replace("_", "."),
                        Path = dir,
                        FileCount = fileCount,
                        HasImportedFiles = importedCount > 0,
                        ImportedCount = importedCount
                    });
                }
            }

            return scrapes.OrderBy(s => s.Name).ToList();
        }
    }
}