// Services/CorporateScrapeImporter.cs - Complete Enhanced Version
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Chunking;
using PrivacyLens.Models;
using PrivacyLens.Services;
using PrivacyLens.DocumentProcessing;
using SharpToken;
using HtmlAgilityPack;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Enum for document sources
    /// </summary>
    public enum DocumentSource
    {
        Unknown,
        WebScrape,
        FileUpload,
        Manual,
        API
    }

    /// <summary>
    /// Enhanced importer with comprehensive metadata extraction and document classification
    /// </summary>
    public sealed class CorporateScrapeImporter
    {
        private readonly GovernanceImportPipeline _pipeline;
        private readonly HybridChunkingOrchestrator _htmlOrchestrator;
        private readonly EmbeddingService _embed;
        private readonly VectorStore _store;
        private readonly IConfiguration _config;
        private readonly ILogger<GptChunkingService> _logger;
        private readonly bool _debugMode;
        private readonly GptEncoding _tokenEncoder;

        public CorporateScrapeImporter(GovernanceImportPipeline pipeline, IConfiguration config)
        {
            _pipeline = pipeline;
            _config = config;
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Initialize tokenizer for statistics
            _tokenEncoder = GptEncoding.GetEncodingForModel("gpt-4");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_debugMode ? LogLevel.Debug : LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<GptChunkingService>();

            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("[DEBUG] Debug mode is ENABLED - verbose logging active");
                Console.ResetColor();
            }

            // TODO: Fix HTML chunking components when implementing full pipeline
            // var boiler = new SimpleBoilerplateFilter();
            // var seg = new HtmlDomSegmenter(boiler);
            // var simple = new SimpleTextChunker();  // This is a static class, can't instantiate
            // var gpt = new GptChunkingService(config, _logger);
            // _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);

            // Embedding + Vector store
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
        }

        // Alternative constructor if being called with UnifiedDocumentProcessor
        public CorporateScrapeImporter(UnifiedDocumentProcessor processor, IConfiguration config, ILogger<CorporateScrapeImporter> logger)
        {
            _config = config;
            _debugMode = config.GetValue<bool>("AzureOpenAI:Diagnostics:Verbose", false);

            // Initialize tokenizer for statistics
            _tokenEncoder = GptEncoding.GetEncodingForModel("gpt-4");

            // Create a logger for GptChunkingService
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_debugMode ? LogLevel.Debug : LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<GptChunkingService>();

            // Initialize services using the processor's pipeline
            // This is a workaround since we don't have the pipeline directly
            // TODO: Refactor this when implementing full pipeline

            // TODO: Fix HTML chunking components when implementing full pipeline
            // var boiler = new SimpleBoilerplateFilter();
            // var seg = new HtmlDomSegmenter(boiler);
            // var simple = new SimpleTextChunker();  // This is a static class, can't instantiate
            // var gpt = new GptChunkingService(config, _logger);
            // _htmlOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);

            // Embedding + Vector store
            _embed = new EmbeddingService(config);
            _store = new VectorStore(config);
        }

        /// <summary>
        /// Import result tracking
        /// </summary>
        public class ImportSession
        {
            public string SessionId { get; set; } = Guid.NewGuid().ToString();
            public DateTime StartTime { get; set; } = DateTime.UtcNow;
            public DateTime? EndTime { get; set; }
            public string ScrapePath { get; set; } = string.Empty;
            public string ScrapeName { get; set; } = string.Empty;
            public List<DocumentImportResult> Documents { get; set; } = new List<DocumentImportResult>();
            public int TotalDocuments => Documents.Count;
            public int SuccessfulImports => Documents.Count(d => d.Success);
            public int FailedImports => Documents.Count(d => !d.Success);
            public int TotalChunks { get; set; }
            public int TotalTokens { get; set; }
            public Dictionary<string, int> DocumentTypeBreakdown { get; set; } = new Dictionary<string, int>();

            public void Complete()
            {
                EndTime = DateTime.UtcNow;
            }

            public TimeSpan Duration => EndTime?.Subtract(StartTime) ?? TimeSpan.Zero;
        }

        public class DocumentImportResult
        {
            public string FilePath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
            public int ChunkCount { get; set; }
            public int TokenCount { get; set; }
            public string? DocumentType { get; set; }
            public DocumentSource Source { get; set; } = DocumentSource.WebScrape;
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Import all documents from a corporate scrape
        /// </summary>
        public async Task<ImportSession> ImportScrapeAsync(
            string scrapePath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var session = new ImportSession
            {
                ScrapePath = scrapePath,
                ScrapeName = Path.GetFileName(scrapePath)
            };

            try
            {
                progress?.Report($"Starting import session {session.SessionId}");
                progress?.Report($"Processing scrape: {session.ScrapeName}");

                // Find all documents in the scrape
                var documentsPath = Path.Combine(scrapePath, "documents");
                var evidencePath = Path.Combine(scrapePath, "evidence");

                var files = new List<string>();

                if (Directory.Exists(documentsPath))
                {
                    files.AddRange(Directory.GetFiles(documentsPath, "*.*", SearchOption.AllDirectories));
                }

                if (Directory.Exists(evidencePath))
                {
                    files.AddRange(Directory.GetFiles(evidencePath, "*.*", SearchOption.AllDirectories));
                }

                progress?.Report($"Found {files.Count} files to process");

                // Group files by type
                var filesByExtension = files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var group in filesByExtension)
                {
                    progress?.Report($"  {group.Key}: {group.Value.Count} files");
                }

                // Process each document
                int current = 0;
                foreach (var file in files)
                {
                    current++;
                    progress?.Report($"Processing {current}/{files.Count}: {Path.GetFileName(file)}");

                    var result = await ProcessDocumentAsync(file, session, progress, ct);
                    session.Documents.Add(result);

                    // Update type breakdown
                    if (result.Success && !string.IsNullOrEmpty(result.DocumentType))
                    {
                        if (!session.DocumentTypeBreakdown.ContainsKey(result.DocumentType))
                            session.DocumentTypeBreakdown[result.DocumentType] = 0;
                        session.DocumentTypeBreakdown[result.DocumentType]++;
                    }
                }

                session.Complete();
                progress?.Report($"Import session completed: {session.SuccessfulImports}/{session.TotalDocuments} successful");

                // Save session report
                await SaveImportReportAsync(session);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import session failed for {ScrapeName}", session.ScrapeName);
                session.Complete();
                throw;
            }
        }

        /// <summary>
        /// Process a single document
        /// </summary>
        private async Task<DocumentImportResult> ProcessDocumentAsync(
            string filePath,
            ImportSession session,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var result = new DocumentImportResult
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Source = DetermineDocumentSource(filePath)
            };

            try
            {
                var extension = Path.GetExtension(filePath).ToLowerInvariant();

                // Skip non-document files
                if (!IsSupportedDocument(extension))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Unsupported file type: {extension}";
                    return result;
                }

                // Extract metadata
                result.Metadata = await ExtractFileMetadataAsync(filePath);

                // Determine processing strategy based on file type
                if (extension == ".html" || extension == ".htm")
                {
                    await ProcessHtmlDocumentAsync(filePath, result, progress, ct);
                }
                else if (extension == ".pdf")
                {
                    await ProcessPdfDocumentAsync(filePath, result, progress, ct);
                }
                else if (extension == ".txt" || extension == ".md")
                {
                    await ProcessTextDocumentAsync(filePath, result, progress, ct);
                }
                else
                {
                    await ProcessGenericDocumentAsync(filePath, result, progress, ct);
                }

                result.Success = true;
                session.TotalChunks += result.ChunkCount;
                session.TotalTokens += result.TokenCount;

                if (_debugMode)
                {
                    Console.WriteLine($"[DEBUG] Processed: {result.FileName}");
                    Console.WriteLine($"        Type: {result.DocumentType ?? "Unknown"}");
                    Console.WriteLine($"        Chunks: {result.ChunkCount}");
                    Console.WriteLine($"        Tokens: {result.TokenCount:N0}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process document: {FileName}", result.FileName);
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Process HTML documents using hybrid chunking
        /// </summary>
        private async Task ProcessHtmlDocumentAsync(
            string filePath,
            DocumentImportResult result,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            // TODO: HTML processing not yet implemented
            Console.WriteLine($"[TODO] Would process HTML document: {result.FileName}");
            result.Success = false;
            result.ErrorMessage = "HTML processing not yet implemented";
            result.ChunkCount = 0;
            result.TokenCount = 0;
            result.DocumentType = "Web Content";

            /*
            var html = await File.ReadAllTextAsync(filePath, ct);
            
            // Use hybrid orchestrator for HTML - method doesn't exist yet
            var chunks = await _htmlOrchestrator.ProcessHtmlAsync(html, filePath);
            
            result.ChunkCount = chunks.Count;
            result.TokenCount = chunks.Sum(c => _tokenEncoder.Encode(c.Content).Count);
            result.DocumentType = "Web Content";

            // Store chunks
            foreach (var chunk in chunks)
            {
                // Generate embedding
                var embedding = await _embed.EmbedAsync(chunk.Content);
                
                // Store in vector database - UpsertAsync doesn't exist
                await _store.UpsertAsync(
                    id: Guid.NewGuid().ToString(),
                    text: chunk.Content,
                    embedding: embedding,
                    metadata: new Dictionary<string, object>
                    {
                        ["source_file"] = result.FileName,
                        ["document_type"] = result.DocumentType,
                        ["chunk_index"] = chunk.ChunkIndex,
                        ["source"] = result.Source.ToString()
                    }
                );
            }
            */
        }

        /// <summary>
        /// Process PDF documents
        /// </summary>
        private async Task ProcessPdfDocumentAsync(
            string filePath,
            DocumentImportResult result,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            // TODO: Fix when full pipeline is implemented
            // The old signature doesn't work anymore:
            // await _pipeline.ImportAsync(filePath, progress, current, totalDocs);

            // For now, use classification only
            if (_pipeline != null)
            {
                var classificationResult = await _pipeline.ClassifyDocumentAsync(filePath, null);
                if (classificationResult.Success)
                {
                    result.DocumentType = classificationResult.DocumentType;
                    result.Success = true;
                    result.ChunkCount = 0; // Not chunking yet
                    result.TokenCount = 0; // Not counting yet

                    Console.WriteLine($"[TODO] Would fully process PDF: {result.FileName} as {result.DocumentType}");
                    Console.WriteLine($"      Confidence: {classificationResult.Confidence:F0}%");
                    if (classificationResult.Evidence?.Any() == true)
                    {
                        Console.WriteLine($"      Evidence: {string.Join(", ", classificationResult.Evidence)}");
                    }
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = classificationResult.Error;
                }
            }
            else
            {
                Console.WriteLine("[TODO] Pipeline not available - would process PDF document here");
                result.Success = false;
                result.ErrorMessage = "Pipeline not initialized";
            }
        }

        /// <summary>
        /// Process text documents
        /// </summary>
        private async Task ProcessTextDocumentAsync(
            string filePath,
            DocumentImportResult result,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            // TODO: Text processing not yet fully implemented
            Console.WriteLine($"[TODO] Would process text document: {result.FileName}");
            result.Success = false;
            result.ErrorMessage = "Text processing not yet implemented";
            result.ChunkCount = 0;
            result.TokenCount = 0;
            result.DocumentType = "Text Document";

            /*
            var text = await File.ReadAllTextAsync(filePath, ct);
            
            // Simple chunking for text files
            var chunks = SimpleTextChunker.ChunkText(text, maxTokens: 512, overlap: 50);
            
            result.ChunkCount = chunks.Count;
            result.TokenCount = chunks.Sum(c => _tokenEncoder.Encode(c).Count);
            result.DocumentType = "Text Document";

            // Store chunks
            int index = 0;
            foreach (var chunk in chunks)
            {
                var embedding = await _embed.EmbedAsync(chunk);
                
                // UpsertAsync doesn't exist
                await _store.UpsertAsync(
                    id: Guid.NewGuid().ToString(),
                    text: chunk,
                    embedding: embedding,
                    metadata: new Dictionary<string, object>
                    {
                        ["source_file"] = result.FileName,
                        ["document_type"] = result.DocumentType,
                        ["chunk_index"] = index++,
                        ["source"] = result.Source.ToString()
                    }
                );
            }
            */
        }

        /// <summary>
        /// Process generic documents (Word, Excel, etc.)
        /// </summary>
        private async Task ProcessGenericDocumentAsync(
            string filePath,
            DocumentImportResult result,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            // TODO: Fix when full pipeline is implemented
            // For now, just use classification
            if (_pipeline != null)
            {
                var classificationResult = await _pipeline.ClassifyDocumentAsync(filePath, null);
                if (classificationResult.Success)
                {
                    result.DocumentType = classificationResult.DocumentType;
                    result.Success = true;
                    result.ChunkCount = 0; // Not chunking yet
                    result.TokenCount = 0; // Not counting yet

                    Console.WriteLine($"[TODO] Would fully process document: {result.FileName} as {result.DocumentType}");
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = classificationResult.Error;
                }
            }
            else
            {
                Console.WriteLine("[TODO] Pipeline not available - would process generic document here");
                result.Success = false;
                result.ErrorMessage = "Pipeline not initialized";
            }
        }

        /// <summary>
        /// Determine the source of a document based on its path
        /// </summary>
        private DocumentSource DetermineDocumentSource(string filePath)
        {
            if (filePath.Contains("evidence"))
                return DocumentSource.WebScrape;
            if (filePath.Contains("documents"))
                return DocumentSource.WebScrape;
            if (filePath.Contains("upload"))
                return DocumentSource.FileUpload;

            return DocumentSource.Unknown;
        }

        /// <summary>
        /// Check if file extension is supported
        /// </summary>
        private bool IsSupportedDocument(string extension)
        {
            var supported = new[]
            {
                ".pdf", ".html", ".htm", ".txt", ".md",
                ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".csv", ".json", ".xml"
            };

            return supported.Contains(extension.ToLowerInvariant());
        }

        /// <summary>
        /// Extract metadata from file
        /// </summary>
        private async Task<Dictionary<string, object>> ExtractFileMetadataAsync(string filePath)
        {
            var metadata = new Dictionary<string, object>();

            try
            {
                var fileInfo = new FileInfo(filePath);
                metadata["file_size"] = fileInfo.Length;
                metadata["created_date"] = fileInfo.CreationTimeUtc;
                metadata["modified_date"] = fileInfo.LastWriteTimeUtc;
                metadata["extension"] = fileInfo.Extension;

                // Calculate file hash
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = md5.ComputeHash(stream);
                    metadata["file_hash"] = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract metadata from {FilePath}", filePath);
            }

            return metadata;
        }

        /// <summary>
        /// Save import session report
        /// </summary>
        private async Task SaveImportReportAsync(ImportSession session)
        {
            try
            {
                var reportPath = Path.Combine(session.ScrapePath, $"import_report_{session.SessionId}.json");

                var report = new
                {
                    session.SessionId,
                    session.StartTime,
                    session.EndTime,
                    session.Duration,
                    session.ScrapeName,
                    Statistics = new
                    {
                        session.TotalDocuments,
                        session.SuccessfulImports,
                        session.FailedImports,
                        session.TotalChunks,
                        session.TotalTokens,
                        AverageChunksPerDocument = session.SuccessfulImports > 0
                            ? session.TotalChunks / session.SuccessfulImports
                            : 0,
                        AverageTokensPerDocument = session.SuccessfulImports > 0
                            ? session.TotalTokens / session.SuccessfulImports
                            : 0
                    },
                    session.DocumentTypeBreakdown,
                    Documents = session.Documents.Select(d => new
                    {
                        d.FileName,
                        d.Success,
                        d.ErrorMessage,
                        d.DocumentType,
                        d.ChunkCount,
                        d.TokenCount,
                        d.Source,
                        d.Timestamp
                    })
                };

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(reportPath, json);

                if (_debugMode)
                {
                    Console.WriteLine($"[DEBUG] Import report saved: {reportPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save import report");
            }
        }

        /// <summary>
        /// Simple text chunker utility
        /// </summary>
        private static class SimpleTextChunker
        {
            public static List<string> ChunkText(string text, int maxTokens = 512, int overlap = 50)
            {
                var chunks = new List<string>();
                var lines = text.Split('\n');
                var currentChunk = new StringBuilder();
                var currentTokenCount = 0;

                foreach (var line in lines)
                {
                    var lineTokens = line.Split(' ').Length; // Rough approximation

                    if (currentTokenCount + lineTokens > maxTokens && currentChunk.Length > 0)
                    {
                        chunks.Add(currentChunk.ToString());

                        // Keep overlap
                        var overlapText = string.Join("\n",
                            currentChunk.ToString().Split('\n').TakeLast(2));
                        currentChunk.Clear();
                        currentChunk.AppendLine(overlapText);
                        currentTokenCount = overlapText.Split(' ').Length;
                    }

                    currentChunk.AppendLine(line);
                    currentTokenCount += lineTokens;
                }

                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString());
                }

                return chunks;
            }
        }
    }
}