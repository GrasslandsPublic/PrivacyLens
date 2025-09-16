// Services/DatabaseBootstrapService.cs
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;

namespace PrivacyLens.Services
{
    public static class DatabaseBootstrapService
    {
        public static async Task InitializeAsync()
        {
            var connectionString = GetAppConnectionString();
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine("❌ ConnectionStrings:PostgresApp missing in appsettings.json");
                return;
            }

            // Get embedding dimensions from config
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var embeddingModel = config["AzureOpenAI:EmbeddingsAgent:EmbeddingDeployment"]?.ToLower()
                ?? config["AzureOpenAI:EmbeddingDeployment"]?.ToLower()
                ?? "";

            var dimensions = embeddingModel switch
            {
                "text-embedding-3-small" => 1536,
                "text-embedding-3-large" => 3072,
                "text-embedding-ada-002" => 1536,
                _ => 3072 // Default to large
            };

            Console.WriteLine($"📊 Configuring for {embeddingModel} ({dimensions} dimensions)");

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var conn = await dataSource.OpenConnectionAsync();

            Console.WriteLine("🔧 Checking/Enabling pgvector extension...");
            await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
                await cmd.ExecuteNonQueryAsync();

            // Make sure this connection knows about the 'vector' type immediately
            await conn.ReloadTypesAsync();

            Console.WriteLine("🏗️  Creating enhanced schema with comprehensive metadata...");

            // Create the enhanced chunks table with all metadata fields
            var ddl = string.Format(@"
-- Main chunks table with enhanced metadata
CREATE TABLE IF NOT EXISTS chunks (
  -- Core fields
  id                BIGSERIAL PRIMARY KEY,
  app_id            TEXT,                     -- NULL = governance, else app identifier
  document_path     TEXT NOT NULL,            -- Original file path
  chunk_index       INT NOT NULL,             -- Position in document
  content           TEXT NOT NULL,            -- The actual text content
  embedding         VECTOR({0}),              -- Embedding vector
  
  -- Document metadata
  document_title    TEXT,                     -- Extracted title or filename
  document_type     TEXT,                     -- pdf, docx, html, etc.
  document_category TEXT,                     -- Policy & Legal, Technical, etc.
  document_hash     TEXT,                     -- SHA-256 for deduplication
  
  -- Document classification (from your DocumentInfo model)
  doc_structure     TEXT,                     -- Hierarchical, Tabular, Linear, Mixed
  sensitivity       TEXT,                     -- Public, Internal, Confidential, Personal
  chunking_strategy TEXT,                     -- Recursive, Structure-Aware, Table-Aware, etc.
  
  -- Source tracking
  source_url        TEXT,                     -- If scraped from web
  source_section    TEXT,                     -- Section/chapter in source document
  page_number       INT,                      -- Page number if applicable
  
  -- Compliance & governance fields
  jurisdiction      TEXT,                     -- Alberta, Canada, etc.
  regulation_refs   TEXT[],                   -- Array of regulation references (PIPA, FOIP, etc.)
  risk_level        TEXT,                     -- High, Medium, Low
  requires_review   BOOLEAN DEFAULT FALSE,    -- Flagged for human review
  
  -- PIA-specific fields
  data_elements     TEXT[],                   -- Personal data elements mentioned
  third_parties     TEXT[],                   -- Third parties mentioned
  retention_period  TEXT,                     -- Data retention info if found
  
  -- Timestamps
  created_at        TIMESTAMPTZ DEFAULT now(),
  updated_at        TIMESTAMPTZ DEFAULT now(),
  indexed_at        TIMESTAMPTZ DEFAULT now(), -- When embedded
  document_date     DATE,                      -- Date of the document itself
  
  -- Quality & tracking
  confidence_score  FLOAT,                     -- Chunking quality score
  token_count       INT,                       -- Number of tokens in chunk
  overlap_previous  INT,                       -- Tokens overlapped with previous chunk
  overlap_next      INT,                       -- Tokens overlapped with next chunk
  
  -- Additional metadata as JSONB for flexibility
  metadata          JSONB DEFAULT '{1}'::jsonb
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_chunks_app_id ON chunks(app_id);
CREATE INDEX IF NOT EXISTS idx_chunks_document_path ON chunks(document_path);
CREATE INDEX IF NOT EXISTS idx_chunks_document_category ON chunks(document_category);
CREATE INDEX IF NOT EXISTS idx_chunks_sensitivity ON chunks(sensitivity);
CREATE INDEX IF NOT EXISTS idx_chunks_created_at ON chunks(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_chunks_requires_review ON chunks(requires_review) WHERE requires_review = true;

-- Full-text search support
ALTER TABLE chunks
  ADD COLUMN IF NOT EXISTS tsv tsvector 
  GENERATED ALWAYS AS (
    to_tsvector('english', 
      coalesce(content, '') || ' ' || 
      coalesce(document_title, '') || ' ' ||
      coalesce(source_section, '')
    )
  ) STORED;

CREATE INDEX IF NOT EXISTS idx_chunks_tsv ON chunks USING GIN (tsv);

-- GIN indexes for array fields
CREATE INDEX IF NOT EXISTS idx_chunks_data_elements ON chunks USING GIN (data_elements);
CREATE INDEX IF NOT EXISTS idx_chunks_third_parties ON chunks USING GIN (third_parties);
CREATE INDEX IF NOT EXISTS idx_chunks_regulation_refs ON chunks USING GIN (regulation_refs);

-- JSONB index for flexible metadata queries
CREATE INDEX IF NOT EXISTS idx_chunks_metadata ON chunks USING GIN (metadata);

-- Create a view for governance documents (for convenience)
CREATE OR REPLACE VIEW governance_chunks AS
  SELECT * FROM chunks WHERE app_id IS NULL;

-- Create a view for application-specific chunks
CREATE OR REPLACE VIEW app_chunks AS
  SELECT * FROM chunks WHERE app_id IS NOT NULL;

-- Add update trigger for updated_at
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $func$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$func$ language 'plpgsql';

DROP TRIGGER IF EXISTS update_chunks_updated_at ON chunks;

CREATE TRIGGER update_chunks_updated_at 
  BEFORE UPDATE ON chunks 
  FOR EACH ROW 
  EXECUTE FUNCTION update_updated_at_column();

-- Document tracking table (optional but useful)
CREATE TABLE IF NOT EXISTS indexed_documents (
  id                BIGSERIAL PRIMARY KEY,
  file_path         TEXT UNIQUE NOT NULL,
  file_hash         TEXT NOT NULL,
  file_size_bytes   BIGINT,
  document_category TEXT,
  app_id            TEXT,
  chunk_count       INT,
  indexed_at        TIMESTAMPTZ DEFAULT now(),
  last_modified     TIMESTAMPTZ,
  metadata          JSONB DEFAULT '{2}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_indexed_docs_app_id ON indexed_documents(app_id);
CREATE INDEX IF NOT EXISTS idx_indexed_docs_category ON indexed_documents(document_category);
", dimensions, "{}", "{}");

            await using (var cmd = new NpgsqlCommand(ddl, conn))
                await cmd.ExecuteNonQueryAsync();

            // Handle vector index creation for high-dimensional embeddings
            Console.WriteLine("🔧 Configuring vector similarity index...");

            if (dimensions > 2000)
            {
                // For text-embedding-3-large (3072 dims), we need IVFFlat
                Console.WriteLine($"  Using IVFFlat index for {dimensions} dimensions");

                // IVFFlat requires data before index creation
                var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM chunks", conn);
                var rowCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

                if (rowCount >= 100)  // Need reasonable amount of data for clustering
                {
                    // Calculate optimal lists parameter (sqrt of row count)
                    int lists = Math.Min(Math.Max((int)Math.Sqrt(rowCount), 100), 1000);

                    var indexSql = string.Format(@"
                        CREATE INDEX IF NOT EXISTS idx_chunks_embedding_ivfflat
                        ON chunks USING ivfflat (embedding vector_cosine_ops)
                        WITH (lists = {0});", lists);

                    await using (var idxCmd = new NpgsqlCommand(indexSql, conn))
                        await idxCmd.ExecuteNonQueryAsync();

                    Console.WriteLine($"✓ Created IVFFlat index with {lists} lists");
                    Console.WriteLine("  Tip: Use 'SET ivfflat.probes = 10;' in queries for better recall");
                }
                else
                {
                    Console.WriteLine("⚠️ IVFFlat index pending - need at least 100 documents first");
                    Console.WriteLine("  Index will be created automatically after initial data load");

                    // Create a function to auto-build index when ready
                    var triggerSql = @"
                        CREATE OR REPLACE FUNCTION auto_create_ivfflat_index() 
                        RETURNS void AS $func$
                        DECLARE
                            row_count bigint;
                            list_count int;
                        BEGIN
                            SELECT COUNT(*) INTO row_count FROM chunks;
                            IF row_count >= 100 AND NOT EXISTS (
                                SELECT 1 FROM pg_indexes 
                                WHERE indexname = 'idx_chunks_embedding_ivfflat'
                            ) THEN
                                list_count := LEAST(GREATEST(SQRT(row_count)::int, 100), 1000);
                                EXECUTE format('CREATE INDEX idx_chunks_embedding_ivfflat ON chunks USING ivfflat (embedding vector_cosine_ops) WITH (lists = %s)', list_count);
                                RAISE NOTICE 'Created IVFFlat index with % lists for % rows', list_count, row_count;
                            END IF;
                        END;
                        $func$ LANGUAGE plpgsql;";

                    await using (var trigCmd = new NpgsqlCommand(triggerSql, conn))
                        await trigCmd.ExecuteNonQueryAsync();

                    Console.WriteLine("✓ Auto-index function created - will build index after 100 documents");
                }
            }
            else
            {
                // For smaller models, use HNSW (better performance)
                var indexSql = @"
                    CREATE INDEX IF NOT EXISTS idx_chunks_embedding_hnsw
                    ON chunks USING hnsw (embedding vector_cosine_ops)
                    WITH (m = 16, ef_construction = 64);";

                await using (var idxCmd = new NpgsqlCommand(indexSql, conn))
                    await idxCmd.ExecuteNonQueryAsync();

                Console.WriteLine($"✓ Created HNSW index for {dimensions} dimensions");
            }

            Console.WriteLine("✅ Enhanced schema created successfully!");

            // Show summary
            Console.WriteLine("\n📋 Database Configuration Summary:");
            Console.WriteLine($"  • Embedding dimensions: {dimensions}");
            Console.WriteLine($"  • Vector index: {(dimensions > 2000 ? "IVFFlat" : "HNSW")} with cosine distance");
            Console.WriteLine($"  • Full-text search: Enabled with GIN index");
            Console.WriteLine($"  • Multi-tenant: app_id field (NULL = governance)");
            Console.WriteLine($"  • Compliance fields: jurisdiction, regulations, risk level");
            Console.WriteLine($"  • PIA fields: data elements, third parties, retention");
            Console.WriteLine($"  • Quality tracking: confidence, token counts, overlaps");
            Console.WriteLine($"  • Flexible metadata: JSONB field for extensions");

            Console.WriteLine("\n✅ Database initialization complete!");
        }

        private static string GetAppConnectionString()
        {
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                var path = Path.Combine(cwd, "appsettings.json");

                if (!File.Exists(path))
                    path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (!File.Exists(path))
                {
                    Console.WriteLine("❌ appsettings.json not found");
                    return string.Empty;
                }

                var json = File.ReadAllText(path);

                try
                {
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                        cs.TryGetProperty("PostgresApp", out var val))
                    {
                        return val.GetString() ?? string.Empty;
                    }
                }
                catch (JsonException je)
                {
                    Console.WriteLine($"❌ Failed to parse appsettings.json: {je.Message}");
                    Console.WriteLine("   Please check for missing commas or extra characters.");
                    return string.Empty;
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