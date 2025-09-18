// File: Models/ProgressUpdate.cs
namespace PrivacyLens.DocumentProcessing.Models
{
    public class ProgressUpdate
    {
        public string Phase { get; set; } = "";
        public string Status { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Detail { get; set; } = "";
        public int? Percentage { get; set; } = "";
        public int? ChunksCreated { get; set; } = "";
    }
}
