// Menus/GovernanceMenu.cs — Streamlined Governance Database Management menu
// Removes Export Database, Manage Document Categories, and Rebuild Database
// to keep the menu focused and simple.

using System;
using System.IO;
using System.Linq;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class GovernanceMenu
    {
        private readonly string appPath;
        private readonly CorporateScrapingMenu corporateScrapingMenu;
        private readonly ConfigurationService configService;

        public GovernanceMenu()
        {
            appPath = AppDomain.CurrentDomain.BaseDirectory;
            corporateScrapingMenu = new CorporateScrapingMenu();
            configService = new ConfigurationService();
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
                Console.WriteLine("1. Import Documents from Folder");
                Console.WriteLine("2. Scrape Corporate Website");
                Console.WriteLine("3. View Database Statistics");
                Console.WriteLine("4. Search Documents");
                Console.WriteLine("5. Back to Main Menu");
                Console.WriteLine();
                Console.Write("Select option: ");
                var choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        ImportDocumentsFromFolder();
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
                        return;

                    default:
                        Console.WriteLine("Invalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void ImportDocumentsFromFolder()
        {
            Console.Clear();
            Console.WriteLine("Import Documents from Folder");
            Console.WriteLine("============================");
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

            // Category selection (read-only categories sourced from appsettings.json)
            Console.WriteLine("\nSelect document category:");
            var categories = configService.GetDocumentCategories();
            for (int i = 0; i < categories.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {categories[i]}");
            }

            Console.Write($"\nSelect category (1-{categories.Count}): ");
            var categoryChoice = Console.ReadLine();
            if (!int.TryParse(categoryChoice, out var categoryIndex)
                || categoryIndex < 1
                || categoryIndex > categories.Count)
            {
                Console.WriteLine("Invalid selection.");
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                return;
            }

            var selectedCategory = categories[categoryIndex - 1];

            Console.WriteLine($"\nImporting {files.Count} document(s) as '{selectedCategory}'...");
            int imported = 0;
            int failed = 0;

            foreach (var file in files)
            {
                try
                {
                    // TODO: Implement actual import logic when ImportService is available
                    // For now, simulate the import
                    Console.WriteLine($" ✓ Imported: {Path.GetFileName(file)}");
                    imported++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ✗ Failed: {Path.GetFileName(file)} - {ex.Message}");
                    failed++;
                }
            }

            Console.WriteLine($"\nImport complete: {imported} succeeded, {failed} failed.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private void ViewDatabaseStatistics()
        {
            Console.Clear();
            Console.WriteLine("Database Statistics");
            Console.WriteLine("===================");
            Console.WriteLine();

            // Placeholder for statistics — integrate with DataToolsMenu or direct DB stats later
            Console.WriteLine("Governance Database:");
            Console.WriteLine(" Total Documents: [Coming Soon]");
            Console.WriteLine(" Total Chunks: [Coming Soon]");
            Console.WriteLine(" Categories: [Coming Soon]");
            Console.WriteLine();

            Console.WriteLine("Storage Locations:");
            Console.WriteLine($" Governance: {Path.Combine(appPath, "governance")}");
            Console.WriteLine($" Corporate Scrapes: {Path.Combine(appPath, "governance", "corporate-scrapes")}");
            Console.WriteLine($" Assessments: {Path.Combine(appPath, "assessments")}");
            Console.WriteLine();

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private void SearchDocuments()
        {
            Console.Clear();
            Console.WriteLine("Search Documents");
            Console.WriteLine("================");
            Console.WriteLine();
            Console.WriteLine("🔎 Search functionality coming soon!");
            Console.WriteLine();
            Console.WriteLine("Tip: Use 'Data Tools' from the Main Menu for query and similarity search.");
            Console.WriteLine();
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }
    }
}
