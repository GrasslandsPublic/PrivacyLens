using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PrivacyLens.Services
{
    public class ConfigurationService
    {
        private readonly string configPath;
        private PrivacyLensConfig config;

        public class PrivacyLensConfig
        {
            public PrivacyLensSettings PrivacyLens { get; set; }
        }

        public class PrivacyLensSettings
        {
            public List<string> DocumentCategories { get; set; } = new List<string>();
            public List<string> DocumentStructures { get; set; } = new List<string>();
            public List<string> ChunkingStrategies { get; set; } = new List<string>();
            public List<string> SupportedFileTypes { get; set; } = new List<string>();
            public PathSettings Paths { get; set; } = new PathSettings();
        }

        public class PathSettings
        {
            public string SourceDocuments { get; set; } = "governance\\Source Documents";
            public string WebContent { get; set; } = "governance\\Web Content";
            public string TempFiles { get; set; } = "governance\\temp";
        }

        public ConfigurationService()
        {
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    // Create default configuration if it doesn't exist
                    CreateDefaultConfiguration();
                }

                var json = File.ReadAllText(configPath);
                config = JsonSerializer.Deserialize<PrivacyLensConfig>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                Console.WriteLine("Using default configuration...");
                config = GetDefaultConfig();
            }
        }

        private void CreateDefaultConfiguration()
        {
            var defaultConfig = GetDefaultConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(defaultConfig, options);
            File.WriteAllText(configPath, json);
            Console.WriteLine($"Created default configuration at: {configPath}");
        }

        private PrivacyLensConfig GetDefaultConfig()
        {
            return new PrivacyLensConfig
            {
                PrivacyLens = new PrivacyLensSettings
                {
                    DocumentCategories = new List<string>
                    {
                        "Policy & Legal",
                        "Operational",
                        "Administrative",
                        "Forms & Templates",
                        "Reports",
                        "Communications",
                        "Financial",
                        "Technical",
                        "Web Content",
                        "Other"
                    },
                    DocumentStructures = new List<string>
                    {
                        "Hierarchical",
                        "Tabular",
                        "Linear",
                        "Mixed",
                        "List-Based"
                    },
                    ChunkingStrategies = new List<string>
                    {
                        "Recursive",
                        "Structure-Aware",
                        "Table-Aware",
                        "Section-Based",
                        "Form-Preserving"
                    },
                    SupportedFileTypes = new List<string>
                    {
                        ".pdf", ".docx", ".doc", ".txt",
                        ".html", ".htm", ".csv", ".xlsx", ".xls"
                    }
                }
            };
        }

        public List<string> GetDocumentCategories() => config?.PrivacyLens?.DocumentCategories ?? new List<string>();
        public List<string> GetDocumentStructures() => config?.PrivacyLens?.DocumentStructures ?? new List<string>();
        public List<string> GetChunkingStrategies() => config?.PrivacyLens?.ChunkingStrategies ?? new List<string>();
        public List<string> GetSupportedFileTypes() => config?.PrivacyLens?.SupportedFileTypes ?? new List<string>();
        public PathSettings GetPaths() => config?.PrivacyLens?.Paths ?? new PathSettings();

        public void ReloadConfiguration()
        {
            LoadConfiguration();
            Console.WriteLine("Configuration reloaded.");
        }
    }
}
