using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Core
{
    public interface IScoringDetector
    {
        string DocumentType { get; }
        int Priority { get; }

        DocumentConfidenceScore DetectWithScoring(
            string content,
            DocumentFeatures features,
            DocumentMetadata metadata);

        bool CanHandleDocument(DocumentMetadata metadata);
    }
}