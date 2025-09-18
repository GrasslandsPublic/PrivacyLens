// File: DocumentScoring/Integration/ConfigurationSetup.cs
namespace DocumentScoring.Integration
{
    /// <summary>
    /// Add to your appsettings.json:
    /// </summary>
    public class ScoringConfiguration
    {
        public bool EnableScoring { get; set; } = true;
        public float ConfidenceThreshold { get; set; } = 85f;
        public bool UseParallelProcessing { get; set; } = true;
        public bool EnableDetailedLogging { get; set; } = false;
        public Dictionary<string, float> CustomThresholds { get; set; } = new();
    }

    /* Add to appsettings.json:
    {
        "DocumentScoring": {
            "EnableScoring": true,
            "ConfidenceThreshold": 85.0,
            "UseParallelProcessing": true,
            "EnableDetailedLogging": false,
            "CustomThresholds": {
                "Policy & Legal": 90.0,
                "Technical": 85.0,
                "Financial": 88.0
            }
        }
    }
    */
}
