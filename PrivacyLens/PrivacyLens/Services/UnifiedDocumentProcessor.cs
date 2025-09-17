// Services/UnifiedDocumentProcessor.cs - Complete Implementation
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrivacyLens.Models;
using PrivacyLens.Chunking;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Unified processor that intelligently routes documents through appropriate pipelines
    /// based on content type and structure
    /// </summary>
    public class UnifiedDocumentProcessor
    {
        private readonly IConfiguration _config;
        private readonly ILogger<UnifiedDocumentProcessor> _logger;
        private readonly GovernanceImportPipeline _governancePipeline;
        private readonly HybridChunkingOrchestrator? _hybridOrchestrator;
        private readonly IEmbeddingService _embeddings;
        private readonly IVectorStore _store;

        // Document processing statistics
        private DateTime? _lastProcessedDate;
        private string? _lastProcessedFile;
        private int _totalProcessed;
        private int _totalErrors;

        public UnifiedDocumentProcessor(
            GovernanceImportPipeline governancePipeline,
            IConfiguration config,
            ILogger<UnifiedDocumentProcessor> logger)
        {
            _governancePipeline = governancePipeline ?? throw new ArgumentNullException(nameof(governancePipeline));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Initialize embedding and store services
            _embeddings = new EmbeddingService(config);
            _store = new VectorStore(config);

            // Try to initialize hybrid orchestrator for HTML/web content
            try
            {
                var boiler = new SimpleBoilerplateFilter();
                var seg = new HtmlDomSegmenter(boiler);
                var simple = new SimpleTextChunker();
                var gptLogger = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Information);
                }).CreateLogger<GptChunkingService>();
                var gpt = new GptChunkingService(config, gptLogger);
                _hybridOrchestrator = new HybridChunkingOrchestrator(seg, simple, gpt, config);
                _logger.LogInformation("Hybrid chunking orchestrator initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not initialize hybrid orchestrator, will use standard pipeline for all content");
                _hybridOrchestrator = null;
            }
        }

        /// <summary>
        /// Process a single document, automatically selecting the appropriate pipeline
        /// </summary>
        public async Task<ProcessingResult> ProcessDocumentAsync(
            string filePath,
            IProgress<ImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            _logger.LogInformation("Processing document: {FileName} (Type: {Extension})", fileName, extension);

            try
            {
                // Update processing date properly
                SetProcessedDate(DateTime.Now);
                _lastProcessedFile = fileName;

                // Route based on file type
                ProcessingResult result;

                if (extension == ".html" && _hybridOrchestrator != null)
                {
                    // Use hybrid pipeline for HTML
                    result = await ProcessHtmlDocumentAsync(filePath, progress, ct);
                }
                else if (IsStructuredDocument(extension))
                {
                    // Use governance pipeline for structured documents
                    result = await ProcessStructuredDocumentAsync(filePath, progress, ct);
                }
                else
                {
                    // Use standard text processing for everything else
                    result = await ProcessTextDocumentAsync(filePath, progress, ct);
                }

                _totalProcessed++;
                _logger.LogInformation("Successfully processed {FileName}: {ChunkCount} chunks created",
                    fileName, result.ChunksCreated);

                return result;
            }
            catch (Exception ex)
            {
                _totalErrors++;
                _logger.LogError(ex, "Error processing document: {FileName}", fileName);
                throw new ProcessingException($"Failed to process {fileName}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Process multiple documents in a folder
        /// </summary>
        public async Task<BatchProcessingResult> ProcessFolderAsync(
            string folderPath,
            bool recursive = true,
            IProgress<BatchProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(folderPath, "*.*", searchOption)
                .Where(f => IsSupportedFile(f))
                .ToList();

            _logger.LogInformation("Processing {Count} documents from {Folder}", files.Count, folderPath);

            var result = new BatchProcessingResult
            {
                StartTime = DateTime.Now,
                TotalFiles = files.Count
            };

            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                var file = files[i];
                var fileName = Path.GetFileName(file);

                progress?.Report(new BatchProgress
                {
                    Current = i + 1,
                    Total = files.Count,
                    CurrentFile = fileName,
                    Stage = "Processing"
                });

                try
                {
                    var fileResult = await ProcessDocumentAsync(file, null, ct);
                    result.SuccessfulFiles.Add(fileName);
                    result.TotalChunksCreated += fileResult.ChunksCreated;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process file: {FileName}", fileName);
                    result.FailedFiles.Add(new FailedFile
                    {
                        FileName = fileName,
                        Error = ex.Message
                    });
                }
            }

            result.EndTime = DateTime.Now;
            return result;
        }

        /// <summary>
        /// Set the processed date with proper DateTime conversion
        /// </summary>
        public void SetProcessedDate(DateTime date)
        {
            _lastProcessedDate = date;
        }

        /// <summary>
        /// Set the processed date from a string with proper conversion
        /// </summary>
        public void SetProcessedDate(string dateString)
        {
            if (!string.IsNullOrWhiteSpace(dateString))
            {
                if (DateTime.TryParse(dateString, out var parsedDate))
                {
                    _lastProcessedDate = parsedDate;
                }
                else
                {
                    _logger.LogWarning("Could not parse date string: {DateString}", dateString);
                    _lastProcessedDate = null;
                }
            }
            else
            {
                _lastProcessedDate = null;
            }
        }

        // Properties for accessing processing statistics
        public DateTime? LastProcessedDate => _lastProcessedDate;
        public string? LastProcessedFile => _lastProcessedFile;
        public int TotalProcessed => _totalProcessed;
        public int TotalErrors => _totalErrors;

        // Private helper methods

        private async Task<ProcessingResult> ProcessHtmlDocumentAsync(
            string filePath, IProgress<ImportProgress>? progress, CancellationToken ct)
        {
            _logger.LogDebug("Using hybrid pipeline for HTML document: {FilePath}", filePath);

            var htmlContent = await File.ReadAllTextAsync(filePath, ct);
            var fileName = Path.GetFileName(filePath);

            // Use hybrid orchestrator for HTML
            var chunks = await _hybridOrchestrator!.ChunkHtmlAsync(htmlContent, fileName, ct);

            // Generate embeddings and save
            var enrichedChunks = new List<ChunkRecord>();
            foreach (var chunk in chunks)
            {
                var embedding = await _embeddings.EmbedAsync(chunk.Content, ct);
                enrichedChunks.Add(chunk with { Embedding = embedding });
            }

            await _store.SaveChunksAsync(enrichedChunks, ct);

            return new ProcessingResult
            {
                Success = true,
                ChunksCreated = chunks.Count,
                ProcessingPipeline = "Hybrid"
            };
        }

        private async Task<ProcessingResult> ProcessStructuredDocumentAsync(
            string filePath, IProgress<ImportProgress>? progress, CancellationToken ct)
        {
            _logger.LogDebug("Using governance pipeline for structured document: {FilePath}", filePath);

            // Use the governance pipeline for PDFs, Word docs, etc.
            await _governancePipeline.ImportAsync(filePath, progress, 1, 1, ct);

            return new ProcessingResult
            {
                Success = true,
                ChunksCreated = -1, // Count not easily available from pipeline
                ProcessingPipeline = "Governance"
            };
        }

        private async Task<ProcessingResult> ProcessTextDocumentAsync(
            string filePath, IProgress<ImportProgress>? progress, CancellationToken ct)
        {
            _logger.LogDebug("Using text pipeline for document: {FilePath}", filePath);

            var text = await File.ReadAllTextAsync(filePath, ct);
            await _governancePipeline.ImportTextAsync(text, filePath, progress, 1, 1, ct);

            return new ProcessingResult
            {
                Success = true,
                ChunksCreated = -1,
                ProcessingPipeline = "Text"
            };
        }

        private bool IsStructuredDocument(string extension)
        {
            return extension switch
            {
                ".pdf" => true,
                ".doc" => true,
                ".docx" => true,
                ".xls" => true,
                ".xlsx" => true,
                ".ppt" => true,
                ".pptx" => true,
                _ => false
            };
        }

        private bool IsSupportedFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedExtensions = new[]
            {
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".txt", ".csv", ".html", ".htm", ".md", ".rtf"
            };
            return supportedExtensions.Contains(ext);
        }
    }

    // Supporting classes for the processor

    public class ProcessingResult
    {
        public bool Success { get; set; }
        public int ChunksCreated { get; set; }
        public string ProcessingPipeline { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    public class BatchProcessingResult
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int TotalFiles { get; set; }
        public List<string> SuccessfulFiles { get; set; } = new List<string>();
        public List<FailedFile> FailedFiles { get; set; } = new List<FailedFile>();
        public int TotalChunksCreated { get; set; }
        public bool Cancelled { get; set; }

        public TimeSpan Duration => EndTime - StartTime;
        public int SuccessCount => SuccessfulFiles.Count;
        public int FailureCount => FailedFiles.Count;
        public double SuccessRate => TotalFiles > 0 ? (double)SuccessCount / TotalFiles * 100 : 0;
    }

    public class FailedFile
    {
        public string FileName { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    public class BatchProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public double PercentComplete => Total > 0 ? (double)Current / Total * 100 : 0;
    }

    public class ProcessingException : Exception
    {
        public ProcessingException(string message) : base(message) { }
        public ProcessingException(string message, Exception innerException) : base(message, innerException) { }
    }
}