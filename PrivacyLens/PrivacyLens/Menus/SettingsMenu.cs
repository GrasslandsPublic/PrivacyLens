using System;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class SettingsMenu
    {
        public void Show()
        {
            bool back = false;

            while (!back)
            {
                Console.Clear();
                Console.WriteLine("========================================");
                Console.WriteLine(" Settings");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("1. Initialize Database (enable pgvector, create tables & indexes)");
                Console.WriteLine("B. Back");
                Console.WriteLine();
                Console.Write("Select an option: ");

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

                switch (choice)
                {
                    case "1":
                        RunInitializeDatabase();
                        break;

                    case "B":
                        back = true;
                        break;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void RunInitializeDatabase()
        {
            Console.Clear();
            Console.WriteLine("Initializing database...\n");

            try
            {
                // Run the async initializer from this sync entry point
                DatabaseBootstrapService.InitializeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Initialization failed: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to return to Settings...");
            Console.ReadKey();
        }
    }
}
