using System.Collections.Generic;

namespace PrivacyLens.DocumentScoring.Models
{
    public class ScoringProfile
    {
        public string DocumentType { get; set; }
        public float MaxPossibleScore { get; set; }

        public Dictionary<string, float> DefinitiveFeatures { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> StructuralFeatures { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> LexicalFeatures { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> ConflictingFeatures { get; set; } = new Dictionary<string, float>();
        public List<string> RequiredFeatures { get; set; } = new List<string>();
    }
}