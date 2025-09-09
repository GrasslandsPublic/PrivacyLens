using System;

namespace PrivacyLens.Menus
{
    public class MainMenu
    {
        private readonly GovernanceMenu governanceMenu = new GovernanceMenu();
        private readonly SettingsMenu settingsMenu = new SettingsMenu();   // <-- add this

        public void Show()
        {
            bool exit = false;
            while (!exit)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" PrivacyLens Assessment System ");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Manage Governance Database");
                Console.WriteLine("2. Create New Assessment");
                Console.WriteLine("3. View Existing Assessments");
                Console.WriteLine("4. Scrape Application Website");
                Console.WriteLine("5. Generate Reports");
                Console.WriteLine("6. Settings");
                Console.WriteLine("7. Exit");
                Console.WriteLine();
                Console.Write("Select an option (1-7): ");
                string? choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        governanceMenu.Show();
                        break;

                    case "2":
                        Console.WriteLine("\nCreate New Assessment (coming soon)");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;

                    case "3":
                        Console.WriteLine("\nView Assessments (coming soon)");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;

                    case "4":
                        Console.WriteLine("\nScrape Website (coming soon)");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;

                    case "5":
                        Console.WriteLine("\nGenerate Reports (coming soon)");
                        Console.WriteLine("Press any key to continue...");
                        Console.ReadKey();
                        break;

                    case "6":
                        settingsMenu.Show();  // <-- opens Settings submenu
                        break;

                    case "7":
                        exit = true;
                        Console.WriteLine("\nGoodbye!");
                        break;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }
    }
}

