using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;
using SharpToken;

namespace PrivacyLens.Chunking
{
    /// <summary>
    /// DOM-aware segmenter:
    /// - Removes boilerplate using IBoilerplateFilter
    /// - Finds main content region (<main> or role="main")
    /// - Builds sections guided by H1/H2/H3 breadcrumbs and block elements
    /// - Merges small adjacent blocks under the same breadcrumb
    /// </summary>
    public sealed class HtmlDomSegmenter : IContentSegmenter
    {
        private readonly IBoilerplateFilter _boilerplate;
        private readonly GptEncoding _enc = GptEncoding.GetEncodingForModel("gpt-4o");

        public HtmlDomSegmenter(IBoilerplateFilter boilerplate) => _boilerplate = boilerplate;

        public IReadOnlyList<ContentSection> Segment(string html, string? sourceUri = null)
        {
            var cleaned = _boilerplate.StripChrome(html);

            var doc = new HtmlDocument();
            doc.LoadHtml(cleaned);

            var main = doc.DocumentNode.SelectSingleNode("//main|//*[@role='main']") ?? doc.DocumentNode;

            var sections = new List<ContentSection>();
            var breadcrumb = new List<string>();

            foreach (var node in main.Descendants())
            {
                // Track heading hierarchy
                if (node.Name is "h1" or "h2" or "h3")
                {
                    UpdateBreadcrumb(breadcrumb, node);
                    continue;
                }

                // Consider block content nodes as atomic text units
                if (node.Name is "p" or "ul" or "ol" or "table" or "pre" or "blockquote")
                {
                    var raw = node.InnerText ?? string.Empty;
                    var text = HtmlEntity.DeEntitize(raw).Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // Collapse whitespace
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

                    var tokens = _enc.CountTokens(text);
                    var title = breadcrumb.LastOrDefault();
                    sections.Add(new ContentSection(text, title, breadcrumb.ToArray(), tokens));
                }
            }

            // Merge small consecutive fragments under same breadcrumb into more coherent sections
            return MergeSmall(sections, tokenMin: 200, tokenMax: 1200);
        }

        private static void UpdateBreadcrumb(List<string> bc, HtmlNode heading)
        {
            int level = heading.Name switch { "h1" => 1, "h2" => 2, "h3" => 3, _ => 3 };
            while (bc.Count >= level) bc.RemoveAt(bc.Count - 1);

            var title = HtmlEntity.DeEntitize(heading.InnerText ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(title)) bc.Add(title);
        }

        private IReadOnlyList<ContentSection> MergeSmall(List<ContentSection> input, int tokenMin, int tokenMax)
        {
            var merged = new List<ContentSection>();
            var buffer = new List<ContentSection>();
            int sum = 0;

            void Flush()
            {
                if (buffer.Count == 0) return;
                var joined = string.Join("\n\n", buffer.Select(b => b.Text));
                var title = buffer.Last().Title;
                var bc = buffer.Last().Breadcrumb;
                merged.Add(new ContentSection(joined, title, bc, _enc.CountTokens(joined)));
                buffer.Clear();
                sum = 0;
            }

            foreach (var s in input)
            {
                if (sum == 0)
                {
                    buffer.Add(s);
                    sum = s.ApproxTokens;
                }
                else if (SequenceEqual(buffer.Last().Breadcrumb, s.Breadcrumb) && sum + s.ApproxTokens <= tokenMax)
                {
                    buffer.Add(s);
                    sum += s.ApproxTokens;

                    if (sum >= tokenMin)
                        Flush();
                }
                else
                {
                    Flush();
                    buffer.Add(s);
                    sum = s.ApproxTokens;
                }
            }

            Flush();
            return merged;
        }

        private static bool SequenceEqual(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i]))
                    return false;
            return true;
        }
    }
}
