namespace PrivacyLens.DocumentScoring.Models
{
    public class ScoringEvidence
    {
        public string Feature { get; set; }
        public string Value { get; set; }
        public EvidenceTier Tier { get; set; }
        public DocumentLocation Location { get; set; }
        public float BaseWeight { get; set; }
        public float LocationMultiplier { get; set; }
        public float FinalScore { get; set; }
        public string Description { get; set; }
    }
}