using System;
using System.Collections.Generic;
using System.Linq;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    public class SimpleInteractiveClassifier
    {
        private readonly ConfigurationService configService;
        private readonly List<string> categories;
        private readonly List<string> structures;
        private readonly List<string> strategies;

        public SimpleInteractiveClassifier()
        {
            configService = new ConfigurationService();
            categories = configService.GetDocumentCategories();
            structures = configService.GetDocumentStructures();
            strategies = configService.GetChunkingStrategies();
        }

        public void ClassifyDocument(DocumentInfo document, int current, int total)
        {
            Console.Clear();
            Console.WriteLine("========================================");
            Console.WriteLine($"  DOCUMENT CLASSIFICATION [{current}/{total}]");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine($"File: {document.FileName}");
            Console.WriteLine($"Size: {document.FileSizeFormatted}");
            Console.WriteLine($"Type: {document.FileType.ToUpper()}");
            Console.WriteLine();

            // Category selection
            string selectedCategory = SelectFromList("DOCUMENT CATEGORY", categories, GetSuggestedCategory(document.FileName));
            // Map string category to enum
            document.Category = MapStringToCategory(selectedCategory);

            // Structure selection - suggestion based on selected category
            string suggestedStructure = GetSuggestedStructure(selectedCategory);
            string selectedStructure = SelectFromList("DOCUMENT STRUCTURE", structures, suggestedStructure);
            // Map string structure to enum
            document.Structure = MapStringToStructure(selectedStructure);

            // Chunking strategy selection
            string strategy = SelectFromList("CHUNKING STRATEGY", strategies, GetSuggestedStrategy(selectedCategory));
            document.AdditionalMetadata["ChunkingStrategy"] = strategy;

            // Quick flags
            Console.WriteLine();
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("QUICK FLAGS (Y/N or Enter to skip):");
            Console.WriteLine("----------------------------------------");

            Console.Write("  Contains tables? ");
            var tablesInput = Console.ReadLine()?.ToUpper();
            if (tablesInput == "Y") document.LikelyContainsTables = true;
            else if (tablesInput == "N") document.LikelyContainsTables = false;

            Console.Write("  Requires structure preservation? ");
            var preserveInput = Console.ReadLine()?.ToUpper();
            if (preserveInput == "Y") document.RequiresStructurePreservation = true;
            else if (preserveInput == "N") document.RequiresStructurePreservation = false;

            // Add notes
            Console.Write("\n  Add notes (optional): ");
            var notes = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(notes))
            {
                document.AdditionalMetadata["Notes"] = notes;
            }

            Console.WriteLine("\n[✓] Classification complete!");
            System.Threading.Thread.Sleep(1000);
        }

        private string SelectFromList(string title, List<string> options, string suggestion = null)
        {
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"{title}:");
            Console.WriteLine("----------------------------------------");

            // Add hints for document structures
            if (title == "DOCUMENT STRUCTURE")
            {
                Console.WriteLine("\nStructure Types:");
                Console.WriteLine("  Hierarchical - Has clear sections, chapters, parts (e.g., Acts, policies)");
                Console.WriteLine("  Tabular      - Mainly tables and data (e.g., financial reports, data sheets)");
                Console.WriteLine("  Linear       - Simple sequential text, no special structure");
                Console.WriteLine("  Mixed        - Combination of text, tables, and sections");
                Console.WriteLine("  List-Based   - Primarily bullet points or numbered lists");
                Console.WriteLine();
            }
            // Add hints for chunking strategies
            else if (title == "CHUNKING STRATEGY")
            {
                Console.WriteLine("\nStrategy Types:");
                Console.WriteLine("  Recursive       - General purpose, works for most documents");
                Console.WriteLine("  Structure-Aware - Preserves document hierarchy (best for legal/policy)");
                Console.WriteLine("  Table-Aware     - Special handling for tables and data");
                Console.WriteLine("  Section-Based   - Chunks by document sections");
                Console.WriteLine("  Form-Preserving - Maintains form structure and fields");
                Console.WriteLine();
            }

            // Display options
            for (int i = 0; i < options.Count; i++)
            {
                var marker = (suggestion != null && options[i] == suggestion) ? " <-- suggested" : "";
                Console.WriteLine($"  {i + 1,2}. {options[i]}{marker}");
            }

            // Get selection
            while (true)
            {
                Console.Write($"\nSelect (1-{options.Count}");
                if (suggestion != null)
                {
                    Console.Write(", or Enter for suggested");
                }
                Console.Write("): ");

                var input = Console.ReadLine();

                // Handle Enter for suggestion
                if (string.IsNullOrEmpty(input) && suggestion != null)
                {
                    return suggestion;
                }

                // Handle numeric selection
                if (int.TryParse(input, out int choice) && choice >= 1 && choice <= options.Count)
                {
                    return options[choice - 1];
                }

                Console.WriteLine("Invalid selection. Please try again.");
            }
        }

        private DocumentCategory MapStringToCategory(string category)
        {
            return category switch
            {
                "Policy & Legal" => DocumentCategory.PolicyLegal,
                "Operational" => DocumentCategory.Operational,
                "Administrative" => DocumentCategory.Form, // Map to closest enum
                "Forms & Templates" => DocumentCategory.Form,
                "Reports" => DocumentCategory.Report,
                "Communications" => DocumentCategory.Correspondence,
                "Financial" => DocumentCategory.Report, // Map to closest enum
                "Technical" => DocumentCategory.Technical,
                "Web Content" => DocumentCategory.Web,
                _ => DocumentCategory.Unknown
            };
        }

        private DocumentStructure MapStringToStructure(string structure)
        {
            return structure switch
            {
                "Hierarchical" => DocumentStructure.Hierarchical,
                "Tabular" => DocumentStructure.Tabular,
                "Linear" => DocumentStructure.Linear,
                "Mixed" => DocumentStructure.Mixed,
                "List-Based" => DocumentStructure.Linear, // Map to closest enum
                _ => DocumentStructure.Unknown
            };
        }

        private string GetSuggestedCategory(string fileName)
        {
            var lower = fileName.ToLower();

            if (lower.Contains("policy") || lower.Contains("act") || lower.Contains("regulation"))
                return "Policy & Legal";
            if (lower.Contains("report") || lower.Contains("analysis"))
                return "Reports";
            if (lower.Contains("form") || lower.Contains("template"))
                return "Forms & Templates";
            if (lower.Contains("manual") || lower.Contains("guide"))
                return "Operational";
            if (lower.Contains("notice") || lower.Contains("fact sheet"))
                return "Communications";

            return categories.FirstOrDefault() ?? "Other";
        }

        private string GetSuggestedStructure(string category)
        {
            return category switch
            {
                "Policy & Legal" => "Hierarchical",      // Legal docs have sections/articles
                "Forms & Templates" => "Mixed",          // Forms have fields and instructions
                "Reports" => "Mixed",                    // Reports often have text and tables
                "Financial" => "Tabular",                // Financial docs are table-heavy
                "Communications" => "Linear",            // Letters/notices are usually linear
                "Operational" => "Hierarchical",         // Manuals have chapters/sections
                "Administrative" => "Linear",            // Admin docs are often simple text
                "Technical" => "Hierarchical",           // Tech docs have sections
                "Web Content" => "Linear",               // Web pages vary but often linear
                _ => "Linear"                           // Default to linear
            };
        }

        private string GetSuggestedStrategy(string category)
        {
            return category switch
            {
                "Policy & Legal" => "Structure-Aware",
                "Forms & Templates" => "Form-Preserving",
                "Reports" => "Table-Aware",
                "Financial" => "Table-Aware",
                _ => "Recursive"
            };
        }

        public bool AskToContinue(int remaining)
        {
            Console.WriteLine($"\n{remaining} documents remaining.");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  [C]ontinue with interactive classification");
            Console.WriteLine("  [A]uto-classify remaining (use automatic mode)");
            Console.WriteLine("  [S]kip remaining documents");
            Console.Write("\nYour choice (C/A/S): ");

            var choice = Console.ReadLine()?.ToUpper();

            if (choice == "S")
            {
                return false; // Stop processing
            }
            else if (choice == "A")
            {
                return false; // Switch to auto mode
            }

            return true; // Continue interactive
        }
    }
}