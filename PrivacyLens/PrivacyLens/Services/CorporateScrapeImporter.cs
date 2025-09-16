using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PrivacyLens.Chunking;
using PrivacyLens.Models;
using PrivacyLens.Services;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Imports a scrape folder:
    ///   • HTML in /webpages → DOM-segment + hybrid chunking → embed + save (with progress)
    ///   • Docs in /documents → pipeline.ImportAsync (with progress)
    ///
    /// Features:
    ///   • PDF validation (skip/quarantine invalid PDFs)
    ///   • Console progress per stage
    ///   • End-of-run summary
    /// </summary>
    public sealed class CorporateScrapeImporter
    {
        private readonly GovernanceImportPipeline _pipeline;
        private readonly HybridChunkingOrchestrator _htmlOrchestrator;
        private readonly EmbeddingService _embed;
        private readonly VectorStore _store;
        private readonly IConfiguration _config;

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;

            // HTML hybrid chunking components
            var boiler = new SimpleBoilerplateFilter();
            var seg = new HtmlDomSegmenter(boiler);
            var simple = new SimpleTextChunker();
            var gpt = new GptChunkingService(config);
            _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);

            // Embedding + Vector store for HTML path
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
        }

        public async Task ImportScrapeAsync(string scrapeRoot, CancellationToken ct = default)
        {
            var pagesDir = Path.Combine(scrapeRoot, "webpages");
            var docsDir = Path.Combine(scrapeRoot, "documents");
            var quarantineDir = Path.Combine(scrapeRoot, "_quarantine");
            Directory.CreateDirectory(quarantineDir);

            int htmlOk = 0, htmlErr = 0;
            int docOk = 0, docSkip = 0, docErr = 0;

            Console.WriteLine("Analyzing scrape folders...\n");

            var htmlFiles = Directory.Exists(pagesDir)
                ? Directory.GetFiles(pagesDir, "*.html", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            var docFiles = Directory.Exists(docsDir)
                ? Directory.GetFiles(docsDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => new[] { ".pdf", ".docx", ".pptx", ".xlsx" }
                        .Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToArray()
                : Array.Empty<string>();

            Console.WriteLine($" HTML pages : {htmlFiles.Length}");
            Console.WriteLine($" Documents  : {docFiles.Length}");
            Console.WriteLine();

            // ===== HTML IMPORT (hybrid chunking → embed → save) =====
            if (htmlFiles.Length > 0)
            {
                Console.WriteLine("Importing HTML pages (hybrid chunking):\n");

                for (int i = 0; i < htmlFiles.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = htmlFiles[i];
                    var name = Path.GetFileName(file);

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{i + 1}/{htmlFiles.Length}] (HTML) {name}");
                    Console.ResetColor();

                    try
                    {
                        var html = await File.ReadAllTextAsync(file, ct);

                        // 1) Chunk HTML using hybrid strategy (DOM + simple/GPT per section)
                        var chunkRecords = await _htmlOrchestrator.ChunkHtmlAsync(html, file, ct);

                        Console.WriteLine($"  • Sections/chunks: {chunkRecords.Count}");

                        // 2) Embed each chunk and save
                        var materialized = new List<ChunkRecord>(chunkRecords.Count);
                        for (int c = 0; c < chunkRecords.Count; c++)
                        {
                            var ch = chunkRecords[c];
                            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

                            var emb = await _embed.EmbedAsync(ch.Content, ct);
                            materialized.Add(ch with { Embedding = emb });

                            if ((c + 1) % Math.Max(1, chunkRecords.Count / 10) == 0 || c == chunkRecords.Count - 1)
                            {
                                Console.WriteLine($"    - Embedded {c + 1}/{chunkRecords.Count}");
                            }
                        }

                        await _store.SaveChunksAsync(materialized, ct);
                        Console.WriteLine("  ✓ Saved chunks to database");
                        htmlOk++;
                    }
                    catch (Exception ex)
                    {
                        htmlErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ HTML import failed: {ex.Message}");
                        Console.ResetColor();
                    }
                }

                Console.WriteLine();
            }

            // ===== DOCUMENT IMPORT (pipeline) =====
            if (docFiles.Length > 0)
            {
                Console.WriteLine("Importing downloaded documents:\n");

                for (int i = 0; i < docFiles.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = docFiles[i];
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToLowerInvariant();

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{i + 1}/{docFiles.Length}] (DOC ) {name}");
                    Console.ResetColor();

                    try
                    {
                        // Validate PDFs up-front to avoid PdfPig "startxref" errors
                        if (ext == ".pdf" && !IsLikelyPdf(file))
                        {
                            docSkip++;
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("  ⚠ Skipping: Not a valid PDF (missing %PDF- header). Moving to _quarantine.");
                            Console.ResetColor();

                            var dest = Path.Combine(quarantineDir, name);
                            SafeMove(file, dest);
                            continue;
                        }

                        var progress = new ConsoleProgress(name);
                        await _pipeline.ImportAsync(file, progress, i + 1, docFiles.Length, ct);
                        docOk++;
                    }
                    catch (Exception ex)
                    {
                        docErr++;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ✗ DOC import failed: {ex.Message}");
                        Console.ResetColor();

                        // Quarantine problematic file to unblock rest of the batch
                        var dest = Path.Combine(quarantineDir, name);
                        SafeMove(file, dest);
                    }
                }

                Console.WriteLine();
            }

            // ===== SUMMARY =====
            Console.WriteLine("========================================");
            Console.WriteLine(" Import Summary");
            Console.WriteLine("========================================");
            Console.WriteLine($" HTML:  OK={htmlOk},  ERR={htmlErr}");
            Console.WriteLine($" DOCS:  OK={docOk},  SKIP={docSkip}, ERR={docErr}");
            Console.WriteLine($" Quarantine: {Directory.GetFiles(quarantineDir, "*", SearchOption.TopDirectoryOnly).Length} file(s)");
            Console.WriteLine("========================================\n");
        }

        private static bool IsLikelyPdf(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < 6) return false;
                Span<byte> sig = stackalloc byte[5];
                _ = fs.Read(sig);
                var hdr = System.Text.Encoding.ASCII.GetString(sig);
                return hdr == "%PDF-";
            }
            catch { return false; }
        }

        private static void SafeMove(string src, string dest)
        {
            try
            {
                if (File.Exists(dest))
                {
                    var name = Path.GetFileNameWithoutExtension(dest);
                    var ext = Path.GetExtension(dest);
                    dest = Path.Combine(Path.GetDirectoryName(dest)!, $"{name}_{DateTime.Now:HHmmssfff}{ext}");
                }
                File.Move(src, dest);
            }
            catch
            {
                // ignore; not fatal
            }
        }

        /// <summary>Console progress reporter that prints pipeline stages.</summary>
        private sealed class ConsoleProgress : IProgress<ImportProgress>
        {
            private readonly string _name;
            private DateTime _last = DateTime.MinValue;

            public ConsoleProgress(string name) => _name = name;

            public void Report(ImportProgress p)
            {
                // Throttle very chatty updates
                if ((DateTime.Now - _last).TotalMilliseconds < 75) return;
                _last = DateTime.Now;

                var info = string.IsNullOrWhiteSpace(p.Info) ? "" : $" :: {p.Info}";
                Console.WriteLine($"  [{p.Current}/{p.Total}] {_name} :: {p.Stage}{info}");
            }
        }
    }
}
