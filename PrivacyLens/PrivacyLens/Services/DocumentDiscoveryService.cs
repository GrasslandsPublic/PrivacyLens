using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    public class DocumentDiscoveryService
    {
        private readonly string sourceDirectory;
        private readonly string manifestPath;
        private readonly DocumentClassifierService classifier;
        private readonly ConfigurationService configService;
        private readonly SimpleInteractiveClassifier interactiveClassifier;

        // Supported file extensions - now loaded from config
        private readonly string[] supportedExtensions;

        public DocumentDiscoveryService()
        {
            // Initialize services
            classifier = new DocumentClassifierService();
            configService = new ConfigurationService();
            interactiveClassifier = new SimpleInteractiveClassifier();

            // Set up paths from config
            var paths = configService.GetPaths();
            string governanceDir = Path.Combine(Directory.GetCurrentDirectory(), "governance");
            sourceDirectory = Path.Combine(governanceDir, "Source Documents");
            manifestPath = Path.Combine(governanceDir, "temp", "manifest.json");

            // Load supported extensions from config
            supportedExtensions = configService.GetSupportedFileTypes().ToArray();

            // Ensure directories exist
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        }

        public DocumentManifest DiscoverDocuments(bool useInteractiveMode = false)
        {
            Console.WriteLine($"Scanning directory: {sourceDirectory}");

            if (useInteractiveMode)
            {
                Console.WriteLine($"Starting INTERACTIVE document discovery...\n");
            }
            else
            {
                Console.WriteLine($"Starting AUTOMATIC document discovery with classification...\n");
            }

            var manifest = new DocumentManifest
            {
                CreatedAt = DateTime.Now,
                SourceDirectory = sourceDirectory
            };

            // Check if directory exists
            if (!Directory.Exists(sourceDirectory))
            {
                Console.WriteLine("Source directory not found. Creating it...");
                Directory.CreateDirectory(sourceDirectory);
                return manifest;
            }

            // Find all supported files
            var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
                .Where(file => supportedExtensions.Any(ext =>
                    file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (files.Count == 0)
            {
                Console.WriteLine("No supported documents found.");
                Console.WriteLine($"Supported types: {string.Join(", ", supportedExtensions)}");
                return manifest;
            }

            Console.WriteLine($"Found {files.Count} files to process\n");

            // Process each file
            int processedCount = 0;
            foreach (var file in files)
            {
                processedCount++;

                var fileInfo = new FileInfo(file);
                var docInfo = new DocumentInfo
                {
                    FileName = fileInfo.Name,
                    FilePath = fileInfo.FullName,
                    FileSizeBytes = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTime,
                    FileType = fileInfo.Extension.ToLower(),
                    DiscoveredAt = DateTime.Now
                };

                if (useInteractiveMode)
                {
                    // Interactive classification using config categories
                    interactiveClassifier.ClassifyDocument(docInfo, processedCount, files.Count);

                    // Ask to continue for large batches
                    if (files.Count > 5 && processedCount % 5 == 0 && processedCount < files.Count)
                    {
                        if (!interactiveClassifier.AskToContinue(files.Count - processedCount))
                        {
                            Console.WriteLine("Auto-classifying remaining documents...");
                            useInteractiveMode = false; // Switch to auto for remaining
                        }
                    }
                }
                else
                {
                    // Automatic classification
                    Console.WriteLine($"[{processedCount}/{files.Count}] Processing: {Path.GetFileName(file)}");

                    docInfo = classifier.ClassifyDocument(docInfo);

                    // Display classification results
                    Console.WriteLine($"    Category: {docInfo.Category}");
                    Console.WriteLine($"    Structure: {docInfo.Structure}");
                    Console.WriteLine($"    Sensitivity: {docInfo.Sensitivity}");
                    Console.WriteLine($"    Recommended Strategy: {docInfo.GetRecommendedChunkingStrategy()}");

                    if (docInfo.LikelyContainsPersonalInfo)
                    {
                        Console.WriteLine("    ⚠️  Likely contains personal information - requires review");
                    }

                    Console.WriteLine();
                }

                manifest.Documents.Add(docInfo);
            }

            // Update manifest statistics
            manifest.UpdateStatistics();

            return manifest;
        }

        public void SaveManifest(DocumentManifest manifest)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                };

                var json = JsonSerializer.Serialize(manifest, options);
                File.WriteAllText(manifestPath, json);
                Console.WriteLine($"Enhanced manifest saved to: {manifestPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving manifest: {ex.Message}");
            }
        }

        public DocumentManifest? LoadManifest()
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    var options = new JsonSerializerOptions
                    {
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    };
                    return JsonSerializer.Deserialize<DocumentManifest>(json, options);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading manifest: {ex.Message}");
            }
            return null;
        }

        public void DisplayResults(DocumentManifest manifest)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("     Enhanced Discovery Results         ");
            Console.WriteLine("========================================");
            Console.WriteLine($"Total documents found: {manifest.TotalDocuments}");
            Console.WriteLine($"Total size: {FormatFileSize(manifest.TotalSizeBytes)}");
            Console.WriteLine($"Documents requiring special handling: {manifest.DocumentsRequiringSpecialHandling}");
            Console.WriteLine($"Source directory: {manifest.SourceDirectory}");
            Console.WriteLine();

            // Display classification breakdown
            if (manifest.CategoryCounts.Any())
            {
                Console.WriteLine("Document Categories:");
                Console.WriteLine("-------------------");
                foreach (var category in manifest.CategoryCounts.OrderByDescending(c => c.Value))
                {
                    Console.WriteLine($"  {category.Key}: {category.Value} documents");
                }
                Console.WriteLine();
            }

            if (manifest.StructureCounts.Any())
            {
                Console.WriteLine("Document Structures:");
                Console.WriteLine("-------------------");
                foreach (var structure in manifest.StructureCounts.OrderByDescending(s => s.Value))
                {
                    Console.WriteLine($"  {structure.Key}: {structure.Value} documents");
                }
                Console.WriteLine();
            }

            if (manifest.SensitivityCounts.Any())
            {
                Console.WriteLine("Content Sensitivity:");
                Console.WriteLine("-------------------");
                foreach (var sensitivity in manifest.SensitivityCounts.OrderByDescending(s => s.Value))
                {
                    var indicator = sensitivity.Key == ContentSensitivity.Personal ? " ⚠️" : "";
                    Console.WriteLine($"  {sensitivity.Key}: {sensitivity.Value} documents{indicator}");
                }
                Console.WriteLine();
            }

            // Display chunking strategy recommendations
            var strategyGroups = manifest.Documents
                .GroupBy(d => d.GetRecommendedChunkingStrategy())
                .OrderByDescending(g => g.Count());

            Console.WriteLine("Recommended Chunking Strategies:");
            Console.WriteLine("--------------------------------");
            foreach (var strategy in strategyGroups)
            {
                Console.WriteLine($"  {strategy.Key}: {strategy.Count()} documents");
            }
            Console.WriteLine();

            // Display sample documents by type
            if (manifest.Documents.Any())
            {
                Console.WriteLine("Sample Documents by Category:");
                Console.WriteLine("-----------------------------");

                var grouped = manifest.Documents.GroupBy(d => d.Category);
                foreach (var group in grouped)
                {
                    Console.WriteLine($"\n{group.Key} ({group.Count()} files):");
                    foreach (var doc in group.Take(3))
                    {
                        Console.WriteLine($"  - {doc.FileName}");
                        Console.WriteLine($"    Size: {doc.FileSizeFormatted} | Structure: {doc.Structure} | Strategy: {doc.GetRecommendedChunkingStrategy()}");
                    }
                    if (group.Count() > 3)
                    {
                        Console.WriteLine($"  ... and {group.Count() - 3} more");
                    }
                }
            }
            else
            {
                Console.WriteLine("No documents found.");
                Console.WriteLine($"Please add supported files to:");
                Console.WriteLine(sourceDirectory);
                Console.WriteLine("\nSupported formats:");
                Console.WriteLine($"  {string.Join(", ", supportedExtensions)}");
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }
    }
}