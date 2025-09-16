// Services/VectorStore.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using PrivacyLens.Models;

namespace PrivacyLens.Services
{
    public sealed class VectorStore : IVectorStore, IAsyncDisposable
    {
        private const string SchemaName = "public";
        private const string TableName = "chunks";

        private readonly NpgsqlDataSource _dataSource;
        private readonly IConfiguration? _config;

        // VectorStore settings
        private readonly bool _enforceFixedDim;
        private readonly int _desiredDim;
        private readonly string _onMismatch;   // truncate | skip | fail

        // ANN settings
        private readonly bool _annBuild;
        private readonly string _annMethod;    // hnsw | ivfflat
        private readonly string _annDistance;  // cosine | l2 | ip
        private readonly int _ivfLists;
        private readonly int _ivfProbes;       // Query-time parameter for IVFFlat
        private readonly int _hnswM;
        private readonly int _hnswEfC;
        private readonly int _hnswEfSearch;    // Query-time parameter for HNSW

        public VectorStore(IConfiguration config)
            : this(config.GetConnectionString("Postgres")
                 ?? config.GetConnectionString("PostgresApp")
                 ?? throw new InvalidOperationException(
                        "ConnectionStrings:Postgres or ConnectionStrings:PostgresApp is missing."),
                   config)
        { }

        public VectorStore(string connectionString, IConfiguration? config = null)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector(); // register pgvector mappings
            _dataSource = builder.Build();
            _config = config ?? null;

            var vs = config?.GetSection("VectorStore");
            _enforceFixedDim = vs?.GetValue("EnforceFixedDim", true) ?? true;
            _desiredDim = vs?.GetValue("DesiredEmbeddingDim", 3072) ?? 3072;
            _onMismatch = vs?["OnFixedDimMismatch"] ?? "fail";

            var ann = vs?.GetSection("Ann");
            _annBuild = ann?.GetValue("Build", true) ?? true;
            _annMethod = (ann?["Method"] ?? "ivfflat").ToLowerInvariant();
            _annDistance = (ann?["Distance"] ?? "cosine").ToLowerInvariant();
            _ivfLists = ann?.GetValue("Lists", 100) ?? 100;
            _ivfProbes = ann?.GetValue("Probes", 10) ?? 10;  // Higher = better recall
            _hnswM = ann?.GetValue("M", 16) ?? 16;
            _hnswEfC = ann?.GetValue("EfConstruction", 200) ?? 200;
            _hnswEfSearch = ann?.GetValue("EfSearch", 100) ?? 100;
        }

        public async Task<bool> CheckDatabaseConnectivityAsync(CancellationToken ct = default)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                var result = await cmd.ExecuteScalarAsync(ct);
                return result != null && Convert.ToInt32(result) == 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Connectivity] Error: {ex.Message}");
                return false;
            }
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // 1) Ensure pgvector extension
            await ExecAsync(conn, "CREATE EXTENSION IF NOT EXISTS vector;", ct);

            // Reload types to recognize vector type
            await conn.ReloadTypesAsync();

            // 2) Check if table exists and get current structure
            var tableExists = await TableExistsAsync(conn, ct);

            if (!tableExists)
            {
                Console.WriteLine("[VectorStore] Creating chunks table with enhanced schema...");

                // Build SQL using string.Format to avoid interpolation issues
                var createSql = string.Format(@"
                CREATE TABLE {0}.{1} (
                    -- Core fields
                    id                BIGSERIAL PRIMARY KEY,
                    app_id            TEXT,
                    document_path     TEXT NOT NULL,
                    chunk_index       INT NOT NULL,
                    content           TEXT NOT NULL,
                    embedding         VECTOR({2}),
                    
                    -- Document metadata
                    document_title    TEXT,
                    document_type     TEXT,
                    document_category TEXT,
                    document_hash     TEXT,
                    
                    -- Classification
                    doc_structure     TEXT,
                    sensitivity       TEXT,
                    chunking_strategy TEXT,
                    
                    -- Source tracking
                    source_url        TEXT,
                    source_section    TEXT,
                    page_number       INT,
                    
                    -- Compliance fields
                    jurisdiction      TEXT,
                    regulation_refs   TEXT[],
                    risk_level        TEXT,
                    requires_review   BOOLEAN DEFAULT FALSE,
                    
                    -- PIA fields
                    data_elements     TEXT[],
                    third_parties     TEXT[],
                    retention_period  TEXT,
                    
                    -- Timestamps
                    created_at        TIMESTAMPTZ DEFAULT now(),
                    updated_at        TIMESTAMPTZ DEFAULT now(),
                    indexed_at        TIMESTAMPTZ DEFAULT now(),
                    document_date     DATE,
                    
                    -- Quality tracking
                    confidence_score  FLOAT,
                    token_count       INT,
                    overlap_previous  INT,
                    overlap_next      INT,
                    
                    -- Flexible metadata
                    metadata          JSONB DEFAULT '{3}'::jsonb
                );", SchemaName, TableName, _desiredDim, "{}");

                await ExecAsync(conn, createSql, ct);
                Console.WriteLine($"[VectorStore] Table created with {_desiredDim} dimensions");
            }

            // 3) Check for IVFFlat index and create if needed
            await EnsureIvfFlatIndexAsync(conn, ct);
        }

        private async Task EnsureIvfFlatIndexAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            if (_desiredDim > 2000)
            {
                // Check if we have enough data for IVFFlat
                var countSql = $"SELECT COUNT(*) FROM {SchemaName}.{TableName}";
                var rowCount = Convert.ToInt64(await ScalarAsync(conn, countSql, ct));

                if (rowCount >= 100)
                {
                    // Check if index already exists
                    var indexExists = await IndexExistsAsync(conn, "idx_chunks_embedding_ivfflat", ct);

                    if (!indexExists)
                    {
                        // Calculate optimal lists (sqrt of row count)
                        int lists = Math.Min(Math.Max((int)Math.Sqrt(rowCount), 100), 1000);

                        Console.WriteLine($"[VectorStore] Creating IVFFlat index with {lists} lists for {rowCount} rows...");

                        var indexSql = $@"
                            CREATE INDEX idx_chunks_embedding_ivfflat
                            ON {SchemaName}.{TableName}
                            USING ivfflat (embedding vector_cosine_ops)
                            WITH (lists = {lists});";

                        await ExecAsync(conn, indexSql, ct);
                        Console.WriteLine("[VectorStore] IVFFlat index created successfully");
                    }
                }
                else if (rowCount > 0)
                {
                    Console.WriteLine($"[VectorStore] Waiting for more data before creating IVFFlat index ({rowCount}/100 documents)");
                }
            }
        }

        /// <summary>
        /// Saves chunks with progress callback for pipeline
        /// </summary>
        public async Task SaveChunksWithProgressAsync(
            IReadOnlyList<ChunkRecord> chunks,
            Action<int, int, string>? onRowSaved = null,
            CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Check if we need to build IVFFlat index after this insert
            var preCount = Convert.ToInt64(await ScalarAsync(conn,
                $"SELECT COUNT(*) FROM {SchemaName}.{TableName}", ct));

            var insertSql = $@"
                INSERT INTO {SchemaName}.{TableName}
                    (document_path, chunk_index, content, embedding, 
                     token_count, created_at, indexed_at)
                VALUES ($1, $2, $3, $4, $5, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)";

            for (int i = 0; i < chunks.Count; i++)
            {
                var ch = chunks[i];
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue(ch.DocumentPath);
                cmd.Parameters.AddWithValue(ch.Index);
                cmd.Parameters.AddWithValue(ch.Content);
                cmd.Parameters.AddWithValue(new Vector(ch.Embedding));

                // Calculate token count (rough estimate: ~4 chars per token)
                var tokenCount = ch.Content.Length / 4;
                cmd.Parameters.AddWithValue(tokenCount);

                await cmd.ExecuteNonQueryAsync(ct);

                onRowSaved?.Invoke(i + 1, chunks.Count,
                    $"{System.IO.Path.GetFileName(ch.DocumentPath)} idx={ch.Index}");
            }

            var postCount = preCount + chunks.Count;

            // Auto-create IVFFlat index if we just crossed the threshold
            if (preCount < 100 && postCount >= 100 && _desiredDim > 2000)
            {
                await EnsureIvfFlatIndexAsync(conn, ct);
            }
        }

        /// <summary>
        /// Saves chunks with enhanced metadata for PIA system
        /// </summary>
        public async Task SaveChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Check if we need to build IVFFlat index after this insert
            var preCount = Convert.ToInt64(await ScalarAsync(conn,
                $"SELECT COUNT(*) FROM {SchemaName}.{TableName}", ct));

            var insertSql = $@"
                INSERT INTO {SchemaName}.{TableName}
                    (document_path, chunk_index, content, embedding, 
                     token_count, created_at, indexed_at)
                VALUES ($1, $2, $3, $4, $5, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)";

            foreach (var ch in chunks)
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue(ch.DocumentPath);
                cmd.Parameters.AddWithValue(ch.Index);
                cmd.Parameters.AddWithValue(ch.Content);
                cmd.Parameters.AddWithValue(new Vector(ch.Embedding));

                // Calculate token count (rough estimate: ~4 chars per token)
                var tokenCount = ch.Content.Length / 4;
                cmd.Parameters.AddWithValue(tokenCount);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            var postCount = preCount + chunks.Count;

            // Auto-create IVFFlat index if we just crossed the threshold
            if (preCount < 100 && postCount >= 100 && _desiredDim > 2000)
            {
                await EnsureIvfFlatIndexAsync(conn, ct);
            }
        }

        /// <summary>
        /// Enhanced vector similarity search with IVFFlat optimization
        /// </summary>
        public async Task<List<SearchResult>> SearchAsync(
            float[] queryVector,
            int topK = 5,
            string? appId = null,
            Dictionary<string, object>? filters = null,
            CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Set IVFFlat probes for better recall (if using IVFFlat)
            if (_desiredDim > 2000)
            {
                await ExecAsync(conn, $"SET ivfflat.probes = {_ivfProbes};", ct);
                Console.WriteLine($"[Search] Using IVFFlat with {_ivfProbes} probes");
            }

            // Build query with optional filters
            var whereClauses = new List<string>();
            var parameters = new List<NpgsqlParameter>();

            // App filter
            if (appId != null)
            {
                whereClauses.Add("app_id = @appId");
                parameters.Add(new NpgsqlParameter("appId", appId));
            }
            else if (appId == "")  // Explicitly searching governance (NULL app_id)
            {
                whereClauses.Add("app_id IS NULL");
            }

            // Additional filters
            if (filters != null)
            {
                foreach (var filter in filters)
                {
                    if (filter.Key == "document_category")
                    {
                        whereClauses.Add("document_category = @category");
                        parameters.Add(new NpgsqlParameter("category", filter.Value));
                    }
                    else if (filter.Key == "sensitivity")
                    {
                        whereClauses.Add("sensitivity = @sensitivity");
                        parameters.Add(new NpgsqlParameter("sensitivity", filter.Value));
                    }
                    else if (filter.Key == "requires_review")
                    {
                        whereClauses.Add("requires_review = @review");
                        parameters.Add(new NpgsqlParameter("review", filter.Value));
                    }
                }
            }

            var whereClause = whereClauses.Count > 0
                ? "WHERE " + string.Join(" AND ", whereClauses)
                : "";

            var searchSql = $@"
                SELECT 
                    id,
                    document_path,
                    chunk_index,
                    content,
                    1 - (embedding <=> @qvec) as similarity,
                    document_title,
                    document_category,
                    source_section,
                    metadata
                FROM {SchemaName}.{TableName}
                {whereClause}
                ORDER BY embedding <=> @qvec
                LIMIT @k";

            await using var cmd = new NpgsqlCommand(searchSql, conn);
            cmd.Parameters.AddWithValue("qvec", new Vector(queryVector));
            cmd.Parameters.AddWithValue("k", topK);
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param);
            }

            var results = new List<SearchResult>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    DocumentPath = reader.GetString(1),
                    ChunkIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    Similarity = reader.GetFloat(4),
                    DocumentTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DocumentCategory = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SourceSection = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Metadata = reader.IsDBNull(8) ? null : reader.GetFieldValue<Dictionary<string, object>?>(8)
                });
            }

            return results;
        }

        /// <summary>
        /// Hybrid search combining vector similarity and full-text search
        /// </summary>
        public async Task<List<SearchResult>> HybridSearchAsync(
            float[] queryVector,
            string textQuery,
            int topK = 5,
            string? appId = null,
            CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Set IVFFlat probes
            if (_desiredDim > 2000)
            {
                await ExecAsync(conn, $"SET ivfflat.probes = {_ivfProbes};", ct);
            }

            var appFilter = appId != null
                ? "app_id = @appId"
                : appId == ""
                    ? "app_id IS NULL"
                    : "TRUE";

            // Combine vector and text search using RRF (Reciprocal Rank Fusion)
            var hybridSql = $@"
                WITH vector_search AS (
                    SELECT id, 
                           embedding <=> @qvec as distance,
                           ROW_NUMBER() OVER (ORDER BY embedding <=> @qvec) as vec_rank
                    FROM {SchemaName}.{TableName}
                    WHERE {appFilter}
                    ORDER BY embedding <=> @qvec
                    LIMIT @k * 2
                ),
                text_search AS (
                    SELECT id,
                           ts_rank(tsv, plainto_tsquery('english', @textq)) as score,
                           ROW_NUMBER() OVER (ORDER BY ts_rank(tsv, plainto_tsquery('english', @textq)) DESC) as text_rank
                    FROM {SchemaName}.{TableName}
                    WHERE {appFilter} AND tsv @@ plainto_tsquery('english', @textq)
                    LIMIT @k * 2
                ),
                combined AS (
                    SELECT 
                        COALESCE(v.id, t.id) as id,
                        1.0 / (60 + COALESCE(v.vec_rank, 1000)) + 
                        1.0 / (60 + COALESCE(t.text_rank, 1000)) as rrf_score
                    FROM vector_search v
                    FULL OUTER JOIN text_search t ON v.id = t.id
                    ORDER BY rrf_score DESC
                    LIMIT @k
                )
                SELECT 
                    c.id,
                    ch.document_path,
                    ch.chunk_index,
                    ch.content,
                    c.rrf_score,
                    ch.document_title,
                    ch.document_category,
                    ch.source_section,
                    ch.metadata
                FROM combined c
                JOIN {SchemaName}.{TableName} ch ON c.id = ch.id
                ORDER BY c.rrf_score DESC";

            await using var cmd = new NpgsqlCommand(hybridSql, conn);
            cmd.Parameters.AddWithValue("qvec", new Vector(queryVector));
            cmd.Parameters.AddWithValue("textq", textQuery);
            cmd.Parameters.AddWithValue("k", topK);
            if (appId != null)
            {
                cmd.Parameters.AddWithValue("appId", appId);
            }

            var results = new List<SearchResult>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    DocumentPath = reader.GetString(1),
                    ChunkIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    Similarity = reader.GetFloat(4),
                    DocumentTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DocumentCategory = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SourceSection = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Metadata = reader.IsDBNull(8) ? null : reader.GetFieldValue<Dictionary<string, object>?>(8)
                });
            }

            return results;
        }

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

        // Helper methods
        private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteScalarAsync(ct);
        }

        private async Task<bool> TableExistsAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            var sql = @"
                SELECT COUNT(*) 
                FROM information_schema.tables 
                WHERE table_schema = @schema AND table_name = @table";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("schema", SchemaName);
            cmd.Parameters.AddWithValue("table", TableName);

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return count > 0;
        }

        private async Task<bool> IndexExistsAsync(NpgsqlConnection conn, string indexName, CancellationToken ct)
        {
            var sql = "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @name";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", indexName);

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return count > 0;
        }
    }

    // Search result model
    public class SearchResult
    {
        public long Id { get; set; }
        public string DocumentPath { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
        public string Content { get; set; } = string.Empty;
        public float Similarity { get; set; }
        public string? DocumentTitle { get; set; }
        public string? DocumentCategory { get; set; }
        public string? SourceSection { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }
}