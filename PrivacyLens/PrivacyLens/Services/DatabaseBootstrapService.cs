using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Pgvector;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Idempotent DB initializer:
    ///  • Ensures pgvector extension is enabled
    ///  • Creates 'chunks' table (VECTOR(1536)) for text-embedding-3-small
    ///  • Adds HNSW ANN index (cosine) and optional full-text (GIN) index
    /// </summary>
    public static class DatabaseBootstrapService
    {
        public static async Task InitializeAsync()
        {
            var appConnString = GetAppConnectionString();
            if (string.IsNullOrWhiteSpace(appConnString))
            {
                Console.WriteLine("❌ ConnectionStrings:PostgresApp missing in appsettings.json");
                return;
            }

            var dsb = new NpgsqlDataSourceBuilder(appConnString);
            dsb.UseVector(); // enable pgvector type mapping
            await using var dataSource = dsb.Build();

            await using var conn = await dataSource.OpenConnectionAsync();

            Console.WriteLine("⚙️  Checking/Enabling pgvector extension...");
            await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
                await cmd.ExecuteNonQueryAsync();

            // Make sure this connection knows about the 'vector' type immediately
            await conn.ReloadTypesAsync();

            Console.WriteLine("⚙️  Creating tables & indexes if missing...");
            var ddl = @"
CREATE TABLE IF NOT EXISTS chunks (
  id             BIGSERIAL PRIMARY KEY,
  app_id         TEXT,                -- NULL => governance
  title          TEXT,
  url            TEXT,
  section_path   TEXT,
  page           INT,
  text           TEXT NOT NULL,
  embedding      VECTOR(1536) NOT NULL,   -- text-embedding-3-small
  jurisdiction   TEXT,
  risk_level     TEXT,
  effective_date DATE,
  created_at     TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS chunks_app_id_idx ON chunks(app_id);

-- ANN (HNSW) for cosine distance (common for text embeddings)
CREATE INDEX IF NOT EXISTS chunks_embedding_hnsw
  ON chunks USING hnsw (embedding vector_cosine_ops)
  WITH (m = 16, ef_construction = 64);

-- Optional: full-text search (hybrid with vector)
ALTER TABLE chunks
  ADD COLUMN IF NOT EXISTS tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', coalesce(text,''))) STORED;

CREATE INDEX IF NOT EXISTS chunks_tsv_gin ON chunks USING GIN (tsv);
";
            await using (var cmd = new NpgsqlCommand(ddl, conn))
                await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("🔎 Smoke test (cosine distance)...");
            await using (var test = new NpgsqlCommand(
                "SELECT '[-1,0,1]'::vector <=> '[-1,0,2]'::vector AS distance;", conn))
            {
                var result = await test.ExecuteScalarAsync();
                Console.WriteLine($"✅ vector extension OK; example cosine distance = {result}");
            }

            Console.WriteLine("✅ Initialize Database complete.");
        }

        private static string GetAppConnectionString()
        {
            try
            {
                // Try the current working directory (bin\Debug\net8.0\appsettings.json)
                var cwd = Directory.GetCurrentDirectory();
                var path = Path.Combine(cwd, "appsettings.json");

                // If not found, try the process base directory
                if (!File.Exists(path))
                    path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (!File.Exists(path))
                    return string.Empty;

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                    cs.TryGetProperty("PostgresApp", out var val))
                {
                    return val.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to read appsettings.json: {ex.Message}");
            }
            return string.Empty;
        }
    }
}
