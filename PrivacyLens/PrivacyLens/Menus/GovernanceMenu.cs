using System;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class GovernanceMenu
    {
        private readonly DocumentDiscoveryService discoveryService;

        public GovernanceMenu()
        {
            discoveryService = new DocumentDiscoveryService();
        }

        public void Show()
        {
            bool back = false;

            while (!back)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine("     Governance Database Management     ");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Import Source Documents (Interactive Classification)");
                Console.WriteLine("2. Scrape Division Websites");
                Console.WriteLine("3. Index All Documents");
                Console.WriteLine("4. View Statistics");
                Console.WriteLine("5. Test Search");
                Console.WriteLine("6. Clear Database");
                Console.WriteLine("7. Back to Main Menu");
                Console.WriteLine();
                Console.Write("Select an option (1-7): ");

                string? choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        RunInteractiveDocumentImport();
                        break;
                    case "2":
                        Console.WriteLine("\nScrape Division Websites (coming soon - Phase 1, Step 2)");
                        Console.WriteLine("Will implement: WebScrapingService with automatic classification");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;
                    case "3":
                        Console.WriteLine("\nIndex All Documents (coming soon - Phase 2)");
                        Console.WriteLine("Will implement: Chunking based on classification strategies");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;
                    case "4":
                        ViewStatistics();
                        break;
                    case "5":
                        Console.WriteLine("\nTest Search (coming soon - Phase 2)");
                        Console.WriteLine("Will implement: Vector search with chunked documents");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;
                    case "6":
                        Console.WriteLine("\nClear Database (coming soon)");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;
                    case "7":
                        back = true;
                        break;
                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void RunInteractiveDocumentImport()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine("  Document Import & Classification      ");
            Console.WriteLine("========================================");
            Console.WriteLine();

            Console.WriteLine("This process will:");
            Console.WriteLine("  • Scan for documents in the Source Documents folder");
            Console.WriteLine("  • Let you classify each document interactively");
            Console.WriteLine("  • Choose categories from your configuration");
            Console.WriteLine("  • Set document structure and chunking strategy");
            Console.WriteLine("  • Save classifications for future processing");
            Console.WriteLine();
            Console.WriteLine("Press any key to start...");
            Console.ReadKey();

            // Always use interactive mode for document import
            var manifest = discoveryService.DiscoverDocuments(useInteractiveMode: true);

            if (manifest.TotalDocuments > 0)
            {
                Console.WriteLine();
                discoveryService.DisplayResults(manifest);
                discoveryService.SaveManifest(manifest);

                Console.WriteLine("\n[✓] Document import complete!");
                Console.WriteLine($"    Classified {manifest.TotalDocuments} documents");

                Console.WriteLine("\nNext steps:");
                Console.WriteLine("  1. Review the classification results above");
                Console.WriteLine("  2. Proceed to web scraping (Option 2) for online content");
                Console.WriteLine("  3. Then index all documents (Option 3) using the selected strategies");
            }
            else
            {
                Console.WriteLine("\nNo documents found in the Source Documents folder.");
                Console.WriteLine("Please add PDF, DOCX, or other supported files and try again.");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private void ViewStatistics()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine("        Document Statistics             ");
            Console.WriteLine("========================================");
            Console.WriteLine();

            var manifest = discoveryService.LoadManifest();

            if (manifest == null || manifest.TotalDocuments == 0)
            {
                Console.WriteLine("No document manifest found.");
                Console.WriteLine("Run 'Import Source Documents' first to generate statistics.");
            }
            else
            {
                discoveryService.DisplayResults(manifest);
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }
    }
}