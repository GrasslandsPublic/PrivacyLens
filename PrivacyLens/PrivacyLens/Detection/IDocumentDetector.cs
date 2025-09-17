using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public interface IDocumentDetector
    {
        string DetectorName { get; }
        int Priority { get; }
        DetectionResult Detect(string content, string fileName = null);
    }
}
