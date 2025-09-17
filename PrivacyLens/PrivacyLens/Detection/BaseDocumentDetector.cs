using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentProcessing.Models;

namespace PrivacyLens.DocumentProcessing.Detection
{
    public abstract class BaseDocumentDetector : IDocumentDetector
    {
        protected readonly ILogger _logger;
        protected const int MaxScanLength = 10000; // Only scan first 10k chars for performance

        public abstract string DetectorName { get; }
        public abstract int Priority { get; }

        protected BaseDocumentDetector(ILogger logger)
        {
            _logger = logger;
        }

        public abstract DetectionResult Detect(string content, string fileName = null);

        protected string GetScanContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            return content.Length > MaxScanLength
                ? content.Substring(0, MaxScanLength)
                : content;
        }

        protected bool HasFileExtension(string fileName, params string[] extensions)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        protected void LogDetection(string result, double confidence, string reasoning)
        {
            _logger.LogDebug("{Detector}: {Result} with confidence {Confidence:P}. Reason: {Reasoning}",
                DetectorName, result, confidence, reasoning);
        }
    }
}