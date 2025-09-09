using PrivacyLens.Menus;

namespace PrivacyLens
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // No DB init here—we’ll run it from Settings ▸ Initialize Database
            var mainMenu = new MainMenu();
            mainMenu.Show();
        }
    }
}
