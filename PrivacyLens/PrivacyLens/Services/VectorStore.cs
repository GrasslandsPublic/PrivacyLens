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
    /// <summary>
    /// Postgres-backed vector store using pgvector and Npgsql 7+ data source pattern.
    /// </summary>
    public sealed class VectorStore : IVectorStore, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        /// <summary>
        /// Constructs the store from configuration. Accepts either:
        ///   - ConnectionStrings:Postgres
        ///   - ConnectionStrings:PostgresApp
        /// The first non-null is used; otherwise throws with a helpful message.
        /// </summary>
        public VectorStore(IConfiguration config)
            : this(config.GetConnectionString("Postgres")
                   ?? config.GetConnectionString("PostgresApp")
                   ?? throw new InvalidOperationException(
                       "ConnectionStrings:Postgres or PostgresApp is missing"))
        {
        }

        /// <summary>
        /// Constructs the store from a raw connection string.
        /// </summary>
        public VectorStore(string connectionString)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();                 // Register pgvector mappings at the data source level
            _dataSource = builder.Build();
        }

        /// <summary>
        /// Ensures the database is ready: installs pgvector extension and creates the chunks table.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Enable pgvector extension if not installed
            await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
                await cmd.ExecuteNonQueryAsync(ct);

            // NOTE: Using 'vector' without a fixed dimension simplifies ingestion across models.
            // If you later add an ANN index, you'll likely want vector(1536) or vector(3072), etc.
            const string createSql = """
                CREATE TABLE IF NOT EXISTS chunks (
                    id            bigserial PRIMARY KEY,
                    document_path text NOT NULL,
                    chunk_index   int  NOT NULL,
                    content       text NOT NULL,
                    embedding     vector
                );
                """;

            await using (var cmd = new NpgsqlCommand(createSql, conn))
                await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Saves chunk rows (with embeddings) to Postgres.
        /// </summary>
        public async Task SaveChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
        {
            if (chunks is null || chunks.Count == 0)
                return;

            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            // Parameterized insert
            const string insertSql =
                "INSERT INTO chunks (document_path, chunk_index, content, embedding) VALUES ($1, $2, $3, $4)";

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

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
    }
}
