using System;
using PrivacyLens.Menus;

namespace PrivacyLens.Menus
{
    public sealed class MainMenu
    {
        private readonly GovernanceMenu _governanceMenu = new();

        public void Show()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" PrivacyLens Assessment System");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Manage Governance Database");
                Console.WriteLine("2. Create New Assessment");
                Console.WriteLine("3. View Existing Assessments");
                Console.WriteLine("4. Scrape Application Website");
                Console.WriteLine("5. Generate Reports");
                Console.WriteLine("6. Settings");
                Console.WriteLine("7. Data Tools");
                Console.WriteLine("8. Exit");
                Console.WriteLine();
                Console.Write("Select an option (1-8): ");
                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        Console.Clear();
                        _governanceMenu.Show();
                        Console.Clear();
                        break;

                    case "2":
                        ShowCreateAssessment();
                        break;

                    case "3":
                        ShowViewAssessments();
                        break;

                    case "4":
                        ShowScrapeWebsite();
                        break;

                    case "5":
                        ShowGenerateReports();
                        break;

                    case "6":
                        ShowSettings();
                        break;

                    case "7":
                        ShowDataTools();
                        break;

                    case "8":
                        return;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void ShowCreateAssessment()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Create New Assessment");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("This feature is coming soon!");
            Console.WriteLine();
            Console.WriteLine("Press any key to return to main menu...");
            Console.ReadKey();
        }

        private void ShowViewAssessments()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" View Existing Assessments");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("This feature is coming soon!");
            Console.WriteLine();
            Console.WriteLine("Press any key to return to main menu...");
            Console.ReadKey();
        }

        private void ShowScrapeWebsite()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Scrape Application Website");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("This feature is coming soon!");
            Console.WriteLine();
            Console.WriteLine("Press any key to return to main menu...");
            Console.ReadKey();
        }

        private void ShowGenerateReports()
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine(" Generate Reports");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("This feature is coming soon!");
            Console.WriteLine();
            Console.WriteLine("Press any key to return to main menu...");
            Console.ReadKey();
        }

        private void ShowSettings()
        {
            var settingsMenu = new SettingsMenu();
            settingsMenu.Show();
        }

        private void ShowDataTools()
        {
            var dataToolsMenu = new DataToolsMenu();
            dataToolsMenu.Show();
        }
    }
}