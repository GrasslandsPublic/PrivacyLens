using System.Collections.Generic;

namespace PrivacyLens.Chunking
{
    public record ContentSection(
        string Text,
        string? Title,
        string[] Breadcrumb, // e.g., ["Policies", "Student Records"]
        int ApproxTokens
    );

    public interface IContentSegmenter
    {
        /// <summary>
        /// Convert raw HTML into an ordered list of logical sections (after boilerplate removal).
        /// </summary>
        IReadOnlyList<ContentSection> Segment(string html, string? sourceUri = null);
    }
}


