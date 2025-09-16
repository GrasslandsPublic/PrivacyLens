// Services/VectorStore.cs
using System;
using System.Collections.Generic;
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

        // VectorStore settings
        private readonly bool _enforceFixedDim;
        private readonly int _desiredDim;
        private readonly string _onMismatch;   // truncate | skip | fail

        // ANN settings
        private readonly bool _annBuild;
        private readonly string _annMethod;    // hnsw | ivfflat
        private readonly string _annDistance;  // cosine | l2 | ip
        private readonly int _ivfLists;
        private readonly int _hnswM;
        private readonly int _hnswEfC;

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

            var vs = config?.GetSection("VectorStore");
            _enforceFixedDim = vs?.GetValue("EnforceFixedDim", false) ?? false;
            _desiredDim = vs?.GetValue("DesiredEmbeddingDim", 3072) ?? 3072;
            _onMismatch = vs?["OnFixedDimMismatch"] ?? "fail";   // truncate|skip|fail

            var ann = vs?.GetSection("Ann");
            _annBuild = ann?.GetValue("Build", false) ?? false;
            _annMethod = (ann?["Method"] ?? "hnsw").ToLowerInvariant();
            _annDistance = (ann?["Distance"] ?? "cosine").ToLowerInvariant();
            _ivfLists = ann?.GetValue("Lists", 100) ?? 100;
            _hnswM = ann?.GetValue("M", 16) ?? 16;
            _hnswEfC = ann?.GetValue("EfConstruction", 200) ?? 200;
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

            // 2) Ensure table exists (does not modify schema if present)
            var createSql = $"""
            CREATE TABLE IF NOT EXISTS {SchemaName}.{TableName} (
                id bigserial PRIMARY KEY,
                document_path text NOT NULL,
                chunk_index int NOT NULL,
                content text NOT NULL,
                embedding vector
            );
            """;
            await ExecAsync(conn, createSql, ct);

            // 3) Ensure required columns exist
            await EnsureColumnsAsync(conn, ct);

            // 4) Enforce FIXED DIM if requested
            if (_enforceFixedDim)
                await EnsureFixedDimensionAsync(conn, ct);

            // 5) Optionally build ANN index (only makes sense with fixed dimension)
            if (_enforceFixedDim && _annBuild)
                await EnsureAnnIndexAsync(conn, ct);
        }

        /// <summary>
        /// Inserts chunk rows with embeddings (no per-row progress).
        /// </summary>
        public async Task SaveChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var insertSql = $@"
                INSERT INTO {SchemaName}.{TableName}
                    (document_path, chunk_index, content, embedding)
                VALUES ($1, $2, $3, $4)";
            foreach (var ch in chunks)
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue(ch.DocumentPath);
                cmd.Parameters.AddWithValue(ch.Index);
                cmd.Parameters.AddWithValue(ch.Content);
                cmd.Parameters.AddWithValue(new Vector(ch.Embedding));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        /// <summary>
        /// Inserts with per-row progress callback (used by pipeline).
        /// </summary>
        public async Task SaveChunksWithProgressAsync(
            IReadOnlyList<ChunkRecord> chunks,
            Action<int, int, string>? onRowSaved = null,
            CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var insertSql = $@"
                INSERT INTO {SchemaName}.{TableName}
                    (document_path, chunk_index, content, embedding)
                VALUES ($1, $2, $3, $4)";

            for (int i = 0; i < chunks.Count; i++)
            {
                var ch = chunks[i];
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue(ch.DocumentPath);
                cmd.Parameters.AddWithValue(ch.Index);
                cmd.Parameters.AddWithValue(ch.Content);
                cmd.Parameters.AddWithValue(new Vector(ch.Embedding));
                await cmd.ExecuteNonQueryAsync(ct);

                onRowSaved?.Invoke(i + 1, chunks.Count,
                    $"{System.IO.Path.GetFileName(ch.DocumentPath)} idx={ch.Index}");
            }
        }

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

        // ---------- Internals ----------

        private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<long> CountRowsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
        {
            var q = $"SELECT COUNT(*) FROM {schema}.{table}";
            await using var cmd = new NpgsqlCommand(q, conn);
            var o = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(o);
        }

        private static async Task<string> GetFormatTypeAsync(NpgsqlConnection conn, string schema, string table, string column, CancellationToken ct)
        {
            const string q = @"
                SELECT pg_catalog.format_type(a.atttypid, a.atttypmod)
                FROM pg_attribute a
                WHERE a.attrelid = (@schema || '.' || @table)::regclass
                  AND a.attname = @column
                  AND a.attnum > 0
                  AND NOT a.attisdropped;";
            await using var cmd = new NpgsqlCommand(q, conn);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);
            cmd.Parameters.AddWithValue("column", column);
            var o = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToString(o) ?? string.Empty; // e.g., "vector", "vector(1536)"
        }

        private static async Task EnsureColumnsAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            var cols = await LoadColumnsAsync(conn, SchemaName, TableName, ct);

            if (!cols.ContainsKey("document_path"))
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ADD COLUMN document_path text", ct);

            if (!cols.ContainsKey("chunk_index"))
            {
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ADD COLUMN chunk_index int NOT NULL DEFAULT 0", ct);
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ALTER COLUMN chunk_index DROP DEFAULT", ct);
            }

            if (!cols.ContainsKey("content"))
            {
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ADD COLUMN content text NOT NULL DEFAULT ''", ct);
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ALTER COLUMN content DROP DEFAULT", ct);
            }

            if (!cols.ContainsKey("embedding"))
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ADD COLUMN embedding vector", ct);
        }

        private async Task EnsureFixedDimensionAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            var fmt = await GetFormatTypeAsync(conn, SchemaName, TableName, "embedding", ct);
            var desiredFmt = $"vector({_desiredDim})";

            if (string.Equals(fmt, desiredFmt, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DB] embedding is already {desiredFmt}");
                return;
            }

            var rowCount = await CountRowsAsync(conn, SchemaName, TableName, ct);

            // If column is dimensionless ("vector") or wrong fixed ("vector(n)")
            if (_onMismatch.Equals("truncate", StringComparison.OrdinalIgnoreCase))
            {
                // Clear data and set fixed dimension
                await ExecAsync(conn, $"TRUNCATE TABLE {SchemaName}.{TableName}", ct);
                await ExecAsync(conn, $"ALTER TABLE {SchemaName}.{TableName} ALTER COLUMN embedding TYPE {desiredFmt}", ct);
                Console.WriteLine($"[DB] Truncated and set embedding column to {desiredFmt}");
            }
            else if (_onMismatch.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[DB] WARNING: embedding column is '{fmt}', expected '{desiredFmt}'. Skipping change (OnFixedDimMismatch=skip).");
            }
            else // fail (default)
            {
                throw new InvalidOperationException(
                    $"Embedding column type is '{fmt}', expected '{desiredFmt}'. " +
                    $"Set VectorStore:OnFixedDimMismatch to 'truncate' to auto-fix, or clear the table before rerun.");
            }
        }

        private async Task EnsureAnnIndexAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            // Pick operator class by distance
            string opClass = _annDistance switch
            {
                "cosine" => "vector_cosine_ops",
                "l2" => "vector_l2_ops",
                "ip" => "vector_ip_ops",
                _ => "vector_cosine_ops"
            };

            // Build index name deterministically
            string idxName = $"chunks_embedding_{_annMethod}_{_annDistance}";

            // Create the appropriate index
            string sql = _annMethod switch
            {
                "ivfflat" => $@"
                    CREATE INDEX IF NOT EXISTS {idxName}
                    ON {SchemaName}.{TableName}
                    USING ivfflat (embedding {opClass})
                    WITH (lists = {_ivfLists});",
                "hnsw" => $@"
                    CREATE INDEX IF NOT EXISTS {idxName}
                    ON {SchemaName}.{TableName}
                    USING hnsw (embedding {opClass})
                    WITH (m = {_hnswM}, ef_construction = {_hnswEfC});",
                _ => throw new InvalidOperationException("VectorStore:Ann:Method must be 'hnsw' or 'ivfflat'")
            };

            await ExecAsync(conn, sql, ct);
            Console.WriteLine($"[DB] ANN index ensured: {idxName} (method={_annMethod}, distance={_annDistance})");
        }

        private sealed record ColumnInfo(string ColumnName, string DataType, string? UdtName);

        private static async Task<Dictionary<string, ColumnInfo>> LoadColumnsAsync(
            NpgsqlConnection conn, string schema, string table, CancellationToken ct)
        {
            const string q = @"
                SELECT
                    c.column_name,
                    c.data_type,
                    t.typname AS udt_name
                FROM information_schema.columns c
                LEFT JOIN pg_catalog.pg_type t
                    ON t.typname = c.udt_name
                WHERE c.table_schema = @schema AND c.table_name = @table
                ORDER BY c.ordinal_position;";
            await using var cmd = new NpgsqlCommand(q, conn);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);

            var result = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var udtName = reader.IsDBNull(2) ? null : reader.GetString(2);
                result[name] = new ColumnInfo(name, type, udtName);
            }
            return result;
        }
    }
}
