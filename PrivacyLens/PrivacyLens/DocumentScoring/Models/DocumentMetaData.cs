using System;
using System.Collections.Generic;

namespace PrivacyLens.DocumentScoring.Models
{
    public class DocumentMetadata
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string FileType { get; set; }
        public string Source { get; set; }
        public string Title { get; set; }
        public Dictionary<string, string> ExtractedFields { get; set; } = new Dictionary<string, string>();
    }
}