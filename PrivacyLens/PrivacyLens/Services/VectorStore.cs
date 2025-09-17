// Services/VectorStore.cs - Fixed with proper NULL handling and search functionality
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Search result from vector similarity search
    /// </summary>
    public class SearchResult
    {
        public long Id { get; set; }
        public string DocumentPath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? DocumentTitle { get; set; }
        public string? DocumentType { get; set; }
        public string? SourceUrl { get; set; }
        public double Similarity { get; set; }
        public int ChunkIndex { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    public sealed class VectorStore : IVectorStore, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly int _desiredDim;
        private readonly string _schemaName;
        private readonly string _tableName;
        private readonly bool _verbose;

        public static string SchemaName => "public";
        public static string TableName => "chunks";

        public VectorStore(IConfiguration config)
        {
            // Try both possible connection string names for compatibility
            var connStr = config.GetConnectionString("PostgresApp")
                ?? config.GetConnectionString("PostgreSQL")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:PostgresApp or ConnectionStrings:PostgreSQL");

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
            dataSourceBuilder.UseVector();
            _dataSource = dataSourceBuilder.Build();

            _desiredDim = config.GetValue<int>("VectorStore:DesiredEmbeddingDim", 3072);
            _schemaName = SchemaName;
            _tableName = TableName;
            _verbose = config.GetValue<bool>("VectorStore:Verbose", false);
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Enable pgvector extension
            Console.WriteLine("📦 Checking/Enabling pgvector extension...");
            await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
                await cmd.ExecuteNonQueryAsync(ct);

            await conn.ReloadTypesAsync();

            Console.WriteLine("🏗️ Creating enhanced schema with comprehensive metadata...");

            // Create the enhanced chunks table with all metadata fields
            var ddl = string.Format(@"
-- Main chunks table with enhanced metadata
CREATE TABLE IF NOT EXISTS {1}.{2} (
  -- Core fields
  id                BIGSERIAL PRIMARY KEY,
  app_id            TEXT,                     -- NULL = governance, else app identifier
  document_path     TEXT NOT NULL,            -- Original file path
  chunk_index       INT NOT NULL,             -- Position in document
  content           TEXT NOT NULL,            -- The actual text content
  embedding         VECTOR({0}),              -- Embedding vector
  
  -- Document metadata (nullable fields use NULL not 'NULL' string)
  document_title    TEXT,                     -- Extracted title or filename
  document_type     TEXT,                     -- pdf, docx, html, etc.
  document_category TEXT,                     -- Policy & Legal, Technical, etc.
  document_hash     TEXT,                     -- SHA-256 for deduplication
  
  -- Document classification
  doc_structure     TEXT,                     -- Hierarchical, Tabular, Linear, Mixed
  sensitivity       TEXT,                     -- Public, Internal, Confidential, Personal
  chunking_strategy TEXT,                     -- Recursive, Structure-Aware, Table-Aware, etc.
  
  -- Source tracking
  source_url        TEXT,                     -- If scraped from web
  source_section    TEXT,                     -- Section/chapter in source document
  page_number       INT,                      -- Page number if applicable
  
  -- Compliance & governance fields
  jurisdiction      TEXT,                     -- Alberta, Canada, etc.
  regulation_refs   TEXT[],                   -- Array of regulation references
  risk_level        TEXT,                     -- High, Medium, Low
  requires_review   BOOLEAN DEFAULT false,
  
  -- Data governance
  data_elements     TEXT[],                   -- Types of data mentioned
  third_parties     TEXT[],                   -- Third parties mentioned
  retention_period  TEXT,                     -- Document retention requirements
  
  -- Timestamps
  created_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  indexed_at        TIMESTAMPTZ,
  document_date     DATE,                     -- Date of the document itself
  
  -- Quality & processing metadata
  confidence_score  FLOAT,                    -- Extraction confidence
  token_count       INT,                      -- Number of tokens in chunk
  overlap_previous  INT,                      -- Overlap tokens with previous chunk
  overlap_next      INT,                      -- Overlap tokens with next chunk
  
  -- Extensible JSON metadata
  metadata          JSONB DEFAULT '{{}}'::jsonb,
  
  -- Full-text search
  tsv               TSVECTOR GENERATED ALWAYS AS (
    setweight(to_tsvector('english', COALESCE(document_title, '')), 'A') ||
    setweight(to_tsvector('english', content), 'B')
  ) STORED
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_{2}_document_path ON {1}.{2} (document_path);
CREATE INDEX IF NOT EXISTS idx_{2}_app_id ON {1}.{2} (app_id);
CREATE INDEX IF NOT EXISTS idx_{2}_document_type ON {1}.{2} (document_type);
CREATE INDEX IF NOT EXISTS idx_{2}_tsv ON {1}.{2} USING GIN (tsv);
CREATE INDEX IF NOT EXISTS idx_{2}_metadata ON {1}.{2} USING GIN (metadata);
CREATE INDEX IF NOT EXISTS idx_{2}_created_at ON {1}.{2} (created_at DESC);
", _desiredDim, _schemaName, _tableName);

            await using (var cmd = new NpgsqlCommand(ddl, conn))
                await cmd.ExecuteNonQueryAsync(ct);

            Console.WriteLine("✅ Schema initialization complete!");
        }

        /// <summary>
        /// Saves chunks with proper NULL handling for metadata
        /// </summary>
        public async Task SaveChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var insertSql = $@"
                INSERT INTO {_schemaName}.{_tableName} (
                    app_id, document_path, chunk_index, content, embedding,
                    document_title, document_type, document_category, document_hash,
                    doc_structure, sensitivity, chunking_strategy,
                    source_url, source_section, page_number,
                    jurisdiction, regulation_refs, risk_level, requires_review,
                    data_elements, third_parties, retention_period,
                    document_date, confidence_score, token_count,
                    overlap_previous, overlap_next, metadata,
                    created_at, updated_at, indexed_at
                ) VALUES (
                    @app_id, @document_path, @chunk_index, @content, @embedding,
                    @document_title, @document_type, @document_category, @document_hash,
                    @doc_structure, @sensitivity, @chunking_strategy,
                    @source_url, @source_section, @page_number,
                    @jurisdiction, @regulation_refs, @risk_level, @requires_review,
                    @data_elements, @third_parties, @retention_period,
                    @document_date, @confidence_score, @token_count,
                    @overlap_previous, @overlap_next, @metadata,
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                )";

            var saved = 0;
            foreach (var chunk in chunks)
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn);

                // Core fields (never null)
                cmd.Parameters.AddWithValue("@app_id", DBNull.Value); // NULL for governance
                cmd.Parameters.AddWithValue("@document_path", chunk.DocumentPath);
                cmd.Parameters.AddWithValue("@chunk_index", chunk.Index);
                cmd.Parameters.AddWithValue("@content", chunk.Content);
                cmd.Parameters.AddWithValue("@embedding", new Vector(chunk.Embedding));

                // Document metadata - use DBNull.Value for actual NULLs
                cmd.Parameters.AddWithValue("@document_title",
                    (object?)chunk.DocumentTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@document_type",
                    (object?)chunk.DocumentType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@document_category",
                    (object?)chunk.DocumentCategory ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@document_hash",
                    (object?)chunk.DocumentHash ?? DBNull.Value);

                // Classification
                cmd.Parameters.AddWithValue("@doc_structure",
                    (object?)chunk.DocStructure ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sensitivity",
                    (object?)chunk.Sensitivity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@chunking_strategy",
                    (object?)chunk.ChunkingStrategy ?? DBNull.Value);

                // Source tracking
                cmd.Parameters.AddWithValue("@source_url",
                    (object?)chunk.SourceUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@source_section",
                    (object?)chunk.SourceSection ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@page_number",
                    (object?)chunk.PageNumber ?? DBNull.Value);

                // Compliance
                cmd.Parameters.AddWithValue("@jurisdiction",
                    (object?)chunk.Jurisdiction ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@regulation_refs",
                    (object?)chunk.RegulationRefs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@risk_level",
                    (object?)chunk.RiskLevel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requires_review",
                    chunk.RequiresReview);

                // Data governance
                cmd.Parameters.AddWithValue("@data_elements",
                    (object?)chunk.DataElements ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@third_parties",
                    (object?)chunk.ThirdParties ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@retention_period",
                    (object?)chunk.RetentionPeriod ?? DBNull.Value);

                // Timing & quality
                cmd.Parameters.AddWithValue("@document_date",
                    (object?)chunk.DocumentDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@confidence_score",
                    (object?)chunk.ConfidenceScore ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@token_count",
                    (object?)chunk.TokenCount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@overlap_previous",
                    (object?)chunk.OverlapPrevious ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@overlap_next",
                    (object?)chunk.OverlapNext ?? DBNull.Value);

                // Metadata JSON - ensure it's a valid JSON object
                var metadataJson = chunk.Metadata != null && chunk.Metadata.Any()
                    ? JsonSerializer.Serialize(chunk.Metadata)
                    : "{}";
                cmd.Parameters.AddWithValue("@metadata",
                    NpgsqlTypes.NpgsqlDbType.Jsonb, metadataJson);

                await cmd.ExecuteNonQueryAsync(ct);
                saved++;

                if (_verbose && saved % 10 == 0)
                {
                    Console.WriteLine($"  Saved {saved}/{chunks.Count} chunks...");
                }
            }

            Console.WriteLine($"✅ Saved {saved} chunks to database");

            // Check if we need to create index
            var totalRows = await GetRowCountAsync(conn, ct);
            if (totalRows >= 100)
            {
                await EnsureIvfFlatIndexAsync(conn, ct);
            }
        }

        private async Task<long> GetRowCountAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            var countSql = $"SELECT COUNT(*) FROM {_schemaName}.{_tableName}";
            await using var cmd = new NpgsqlCommand(countSql, conn);
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result);
        }

        private async Task EnsureIvfFlatIndexAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            // Check if index exists
            var checkSql = @"
                SELECT EXISTS (
                    SELECT 1 FROM pg_indexes 
                    WHERE schemaname = @schema 
                    AND tablename = @table 
                    AND indexname = @index
                )";

            await using var checkCmd = new NpgsqlCommand(checkSql, conn);
            checkCmd.Parameters.AddWithValue("@schema", _schemaName);
            checkCmd.Parameters.AddWithValue("@table", _tableName);
            checkCmd.Parameters.AddWithValue("@index", "idx_chunks_embedding_ivfflat");

            var exists = (bool)(await checkCmd.ExecuteScalarAsync(ct) ?? false);

            if (!exists && _desiredDim <= 2000) // IVFFlat has dimension limit
            {
                var rowCount = await GetRowCountAsync(conn, ct);
                int lists = Math.Min(Math.Max((int)Math.Sqrt(rowCount), 100), 1000);

                Console.WriteLine($"📊 Creating IVFFlat index with {lists} lists for {rowCount} rows...");

                var indexSql = $@"
                    CREATE INDEX idx_chunks_embedding_ivfflat
                    ON {_schemaName}.{_tableName}
                    USING ivfflat (embedding vector_cosine_ops)
                    WITH (lists = {lists})";

                await using var indexCmd = new NpgsqlCommand(indexSql, conn);
                await indexCmd.ExecuteNonQueryAsync(ct);

                Console.WriteLine("✅ IVFFlat index created successfully");
            }
        }

        public async Task<NpgsqlConnection> CreateConnectionAsync()
        {
            return await _dataSource.OpenConnectionAsync();
        }

        /// <summary>
        /// Search for similar chunks using vector similarity
        /// </summary>
        public async Task<List<SearchResult>> SearchAsync(
            float[] queryVector,
            int topK = 10,
            string? appId = null,
            CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Build the search query
            var searchSql = $@"
                SELECT 
                    id,
                    document_path,
                    chunk_index,
                    content,
                    document_title,
                    document_type,
                    source_url,
                    metadata,
                    1 - (embedding <=> @query_vector) as similarity
                FROM {_schemaName}.{_tableName}
                WHERE (@app_id IS NULL AND app_id IS NULL) OR (app_id = @app_id)
                ORDER BY embedding <=> @query_vector
                LIMIT @limit";

            await using var cmd = new NpgsqlCommand(searchSql, conn);
            cmd.Parameters.AddWithValue("@query_vector", new Vector(queryVector));
            cmd.Parameters.AddWithValue("@app_id", (object?)appId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", topK);

            var results = new List<SearchResult>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var result = new SearchResult
                {
                    Id = reader.GetInt64(0),
                    DocumentPath = reader.GetString(1),
                    ChunkIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    DocumentTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
                    DocumentType = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Similarity = reader.GetDouble(8)
                };

                // Parse metadata JSON if present
                if (!reader.IsDBNull(7))
                {
                    var metadataJson = reader.GetString(7);
                    try
                    {
                        result.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
                    }
                    catch
                    {
                        // Ignore JSON parsing errors
                    }
                }

                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Check database connectivity
        /// </summary>
        public async Task<bool> CheckDatabaseConnectivityAsync()
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync();

                // Simple connectivity test
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                var result = await cmd.ExecuteScalarAsync();

                return result != null && Convert.ToInt32(result) == 1;
            }
            catch (Exception ex)
            {
                if (_verbose)
                {
                    Console.WriteLine($"[VectorStore] Database connectivity check failed: {ex.Message}");
                }
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
        }
    }
}