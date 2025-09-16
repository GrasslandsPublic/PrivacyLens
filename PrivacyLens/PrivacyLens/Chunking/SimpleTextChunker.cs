using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SharpToken;

namespace PrivacyLens.Chunking
{
    /// <summary>
    /// Fast, rule-based chunker:
    /// - Splits by blank-line paragraphs
    /// - Packs paragraphs into ~target token windows
    /// - Adds small overlap to preserve context
    /// </summary>
    public sealed class SimpleTextChunker : ITextChunker
    {
        private readonly GptEncoding _enc = GptEncoding.GetEncodingForModel("gpt-4o");

        public IReadOnlyList<string> Chunk(string text, int targetTokens = 500, int overlapTokens = 80)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

            var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n")
                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                  .ToArray();

            var chunks = new List<string>();
            var buffer = new List<string>();
            int tok = 0;

            foreach (var p in paragraphs)
            {
                var ptok = _enc.CountTokens(p);

                // If adding this paragraph would exceed target, flush first
                if (tok > 0 && tok + ptok > targetTokens)
                {
                    Flush();
                    buffer.Add(p);
                    tok = ptok;
                }
                else
                {
                    buffer.Add(p);
                    tok += ptok;
                }
            }

            Flush();
            return chunks;

            void Flush()
            {
                if (buffer.Count == 0) return;

                var joined = string.Join("\n\n", buffer);
                chunks.Add(joined);

                // Simple overlap: carry the last small paragraph
                var last = buffer.Last();
                buffer.Clear();

                var ltok = _enc.CountTokens(last);
                if (ltok <= overlapTokens)
                {
                    buffer.Add(last);
                    tok = ltok;
                }
                else
                {
                    tok = 0;
                }
            }
        }
    }
}
