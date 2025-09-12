// Menus/MainMenu.cs (relevant excerpt)
using PrivacyLens.Menus;

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
            Console.WriteLine("7. Exit");
            Console.WriteLine();
            Console.Write("Select an option (1-7): ");
            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":
                    Console.Clear();              // <<< clear before showing the sub-menu
                    _governanceMenu.Show();       // blocks until user selects 0 in sub-menu
                    // Optional: clear on return, so main menu is fresh
                    Console.Clear();
                    break;

                case "7":
                    return;

                default:
                    // handle others...
                    break;
            }
        }
    }
}
