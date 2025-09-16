using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public class DataToolsMenu
    {
        private readonly IConfiguration _config;
        private readonly string _connectionString;

        public DataToolsMenu()
        {
            _config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            _connectionString = _config.GetConnectionString("PostgresApp")
                ?? throw new InvalidOperationException("PostgresApp connection string not found");
        }

        public void Show()
        {
            var back = false;
            while (!back)
            {
                Console.Clear();
                PrintHeader();
                PrintMenu();

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

                switch (choice)
                {
                    case "1":
                        QueryRawChunks();
                        break;

                    case "2":
                        SearchByText();
                        break;

                    case "3":
                        SearchBySimilarity();
                        break;

                    case "4":
                        ShowDatabaseStats();
                        break;

                    case "5":
                        ExportChunksToFile();
                        break;

                    case "6":
                        TestVectorSimilarity();
                        break;

                    case "B":
                        back = true;
                        break;

                    default:
                        Console.WriteLine("\nInvalid option. Press any key to continue...");
                        Console.ReadKey();
                        break;
                }
            }
        }

        private void PrintHeader()
        {
            Console.WriteLine("╔════════════════════════════════════════╗");
            Console.WriteLine("║          DATA TOOLS MENU              ║");
            Console.WriteLine("╚════════════════════════════════════════╝");
            Console.WriteLine();
        }

        private void PrintMenu()
        {
            Console.WriteLine("Database Query Tools:");
            Console.WriteLine("  1. Query Raw Chunks (Browse stored documents)");
            Console.WriteLine("  2. Search by Text (Full-text search)");
            Console.WriteLine("  3. Search by Similarity (Vector search with query)");
            Console.WriteLine();
            Console.WriteLine("Analysis Tools:");
            Console.WriteLine("  4. Show Database Statistics");
            Console.WriteLine("  5. Export Chunks to File");
            Console.WriteLine("  6. Test Vector Similarity");
            Console.WriteLine();
            Console.WriteLine("  [B]ack to Main Menu");
            Console.WriteLine();
            Console.Write("Select an option: ");
        }

        // ============= Query Raw Chunks =============
        private void QueryRawChunks()
        {
            Console.Clear();
            Console.WriteLine("=== Query Raw Chunks ===\n");

            try
            {
                var task = QueryRawChunksAsync();
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task QueryRawChunksAsync()
        {
            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            // First, get count
            await using var countCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM chunks", conn);
            var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            Console.WriteLine($"Total chunks in database: {totalCount}\n");

            if (totalCount == 0)
            {
                Console.WriteLine("No chunks found. Have you indexed any documents yet?");
                return;
            }

            // Get sample chunks
            Console.Write("How many chunks to display? (default 10): ");
            var input = Console.ReadLine();
            int limit = string.IsNullOrWhiteSpace(input) ? 10 : int.Parse(input);

            var query = @"
                SELECT id, document_path, chunk_index, 
                       LEFT(content, 200) as content_preview,
                       array_length(embedding::real[], 1) as embedding_dim
                FROM chunks 
                ORDER BY id DESC
                LIMIT @limit";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync();
            int count = 0;

            while (await reader.ReadAsync())
            {
                count++;
                Console.WriteLine($"--- Chunk #{count} ---");
                Console.WriteLine($"ID: {reader.GetInt64(0)}");
                Console.WriteLine($"Document: {reader.GetString(1)}");
                Console.WriteLine($"Chunk Index: {reader.GetInt32(2)}");
                Console.WriteLine($"Content Preview: {reader.GetString(3)}...");
                Console.WriteLine($"Embedding Dimensions: {reader.GetValue(4)}");
                Console.WriteLine();
            }
        }

        // ============= Search by Text =============
        private void SearchByText()
        {
            Console.Clear();
            Console.WriteLine("=== Full-Text Search ===\n");

            Console.Write("Enter search term(s): ");
            var searchTerm = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                Console.WriteLine("Search term cannot be empty.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            try
            {
                var task = SearchByTextAsync(searchTerm);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task SearchByTextAsync(string searchTerm)
        {
            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            // Simple ILIKE search - could be enhanced with full-text search
            var query = @"
                SELECT id, document_path, chunk_index,
                       content,
                       array_length(embedding::real[], 1) as embedding_dim
                FROM chunks
                WHERE content ILIKE @searchPattern
                LIMIT 10";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("searchPattern", $"%{searchTerm}%");

            await using var reader = await cmd.ExecuteReaderAsync();
            int count = 0;

            Console.WriteLine($"\nSearch results for: '{searchTerm}'\n");

            while (await reader.ReadAsync())
            {
                count++;
                Console.WriteLine($"--- Result #{count} ---");
                Console.WriteLine($"Document: {reader.GetString(1)}");
                Console.WriteLine($"Chunk Index: {reader.GetInt32(2)}");

                // Highlight the search term in content
                var content = reader.GetString(3);
                var truncated = content.Length > 300 ? content.Substring(0, 300) + "..." : content;
                Console.WriteLine($"Content: {truncated}");
                Console.WriteLine();
            }

            if (count == 0)
            {
                Console.WriteLine("No matching chunks found.");
            }
            else
            {
                Console.WriteLine($"Found {count} matching chunk(s).");
            }
        }

        // ============= Search by Similarity =============
        private void SearchBySimilarity()
        {
            Console.Clear();
            Console.WriteLine("=== Vector Similarity Search ===\n");
            Console.WriteLine("This will find chunks semantically similar to your query.\n");

            Console.Write("Enter your search query: ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine("Query cannot be empty.");
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                return;
            }

            try
            {
                var task = SearchBySimilarityAsync(query);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task SearchBySimilarityAsync(string query)
        {
            Console.WriteLine("\nGenerating embedding for your query...");

            // Get embedding for the query
            var embeddingService = new EmbeddingService(_config);
            var queryEmbedding = await embeddingService.EmbedAsync(query);

            Console.WriteLine($"Query embedding generated ({queryEmbedding.Length} dimensions)");
            Console.WriteLine("\nSearching for similar chunks...\n");

            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            // Use cosine similarity search
            var searchQuery = @"
                SELECT id, document_path, chunk_index,
                       content,
                       1 - (embedding <=> @queryVec) as similarity
                FROM chunks
                ORDER BY embedding <=> @queryVec
                LIMIT 5";

            await using var cmd = new NpgsqlCommand(searchQuery, conn);
            cmd.Parameters.AddWithValue("queryVec", new Vector(queryEmbedding));

            await using var reader = await cmd.ExecuteReaderAsync();
            int count = 0;

            while (await reader.ReadAsync())
            {
                count++;
                var similarity = reader.GetFloat(4);

                Console.WriteLine($"--- Result #{count} (Similarity: {similarity:P2}) ---");
                Console.WriteLine($"Document: {reader.GetString(1)}");
                Console.WriteLine($"Chunk Index: {reader.GetInt32(2)}");

                var content = reader.GetString(3);
                var truncated = content.Length > 300 ? content.Substring(0, 300) + "..." : content;
                Console.WriteLine($"Content: {truncated}");
                Console.WriteLine();
            }
        }

        // ============= Show Database Statistics =============
        private void ShowDatabaseStats()
        {
            Console.Clear();
            Console.WriteLine("=== Database Statistics ===\n");

            try
            {
                var task = ShowDatabaseStatsAsync();
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task ShowDatabaseStatsAsync()
        {
            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            // Get various statistics
            var statsQuery = @"
                SELECT 
                    COUNT(*) as total_chunks,
                    COUNT(DISTINCT document_path) as unique_documents,
                    AVG(LENGTH(content)) as avg_content_length,
                    MAX(LENGTH(content)) as max_content_length,
                    MIN(LENGTH(content)) as min_content_length,
                    pg_size_pretty(pg_total_relation_size('chunks')) as table_size
                FROM chunks";

            await using var cmd = new NpgsqlCommand(statsQuery, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                Console.WriteLine($"Total Chunks: {reader.GetInt64(0):N0}");
                Console.WriteLine($"Unique Documents: {reader.GetInt64(1):N0}");
                Console.WriteLine($"Average Chunk Length: {reader.GetDouble(2):N0} characters");
                Console.WriteLine($"Max Chunk Length: {reader.GetInt32(3):N0} characters");
                Console.WriteLine($"Min Chunk Length: {reader.GetInt32(4):N0} characters");
                Console.WriteLine($"Table Size: {reader.GetString(5)}");
            }

            // Get document distribution
            Console.WriteLine("\n--- Document Distribution ---");

            var distQuery = @"
                SELECT document_path, COUNT(*) as chunk_count
                FROM chunks
                GROUP BY document_path
                ORDER BY chunk_count DESC
                LIMIT 10";

            await using var distCmd = new NpgsqlCommand(distQuery, conn);
            await using var distReader = await distCmd.ExecuteReaderAsync();

            while (await distReader.ReadAsync())
            {
                var docPath = distReader.GetString(0);
                var chunkCount = distReader.GetInt64(1);
                var fileName = System.IO.Path.GetFileName(docPath);
                Console.WriteLine($"  {fileName}: {chunkCount} chunks");
            }
        }

        // ============= Export Chunks to File =============
        private void ExportChunksToFile()
        {
            Console.Clear();
            Console.WriteLine("=== Export Chunks to File ===\n");

            Console.Write("Enter output filename (default: chunks_export.txt): ");
            var filename = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(filename))
                filename = "chunks_export.txt";

            try
            {
                var task = ExportChunksToFileAsync(filename);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task ExportChunksToFileAsync(string filename)
        {
            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            var query = @"
                SELECT id, document_path, chunk_index, content
                FROM chunks
                ORDER BY document_path, chunk_index";

            await using var cmd = new NpgsqlCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            using var writer = new System.IO.StreamWriter(filename);
            int count = 0;

            while (await reader.ReadAsync())
            {
                count++;
                await writer.WriteLineAsync($"=== Chunk ID: {reader.GetInt64(0)} ===");
                await writer.WriteLineAsync($"Document: {reader.GetString(1)}");
                await writer.WriteLineAsync($"Chunk Index: {reader.GetInt32(2)}");
                await writer.WriteLineAsync($"Content:");
                await writer.WriteLineAsync(reader.GetString(3));
                await writer.WriteLineAsync();
            }

            Console.WriteLine($"Exported {count} chunks to {filename}");
        }

        // ============= Test Vector Similarity =============
        private void TestVectorSimilarity()
        {
            Console.Clear();
            Console.WriteLine("=== Test Vector Similarity ===\n");
            Console.WriteLine("This tool shows how similar different chunks are to each other.\n");

            try
            {
                var task = TestVectorSimilarityAsync();
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey();
        }

        private async Task TestVectorSimilarityAsync()
        {
            await using var dataSource = CreateDataSource();
            await using var conn = await dataSource.OpenConnectionAsync();

            // Get a random chunk to compare against others
            var randomQuery = @"
                SELECT id, document_path, chunk_index, 
                       LEFT(content, 100) as preview, 
                       embedding
                FROM chunks
                OFFSET floor(random() * (SELECT COUNT(*) FROM chunks))
                LIMIT 1";

            await using var randomCmd = new NpgsqlCommand(randomQuery, conn);
            await using var randomReader = await randomCmd.ExecuteReaderAsync();

            if (!await randomReader.ReadAsync())
            {
                Console.WriteLine("No chunks found in database.");
                return;
            }

            var baseId = randomReader.GetInt64(0);
            var basePath = randomReader.GetString(1);
            var baseIndex = randomReader.GetInt32(2);
            var basePreview = randomReader.GetString(3);
            var baseEmbedding = (Vector)randomReader.GetValue(4);

            // Close the first reader before opening the second
            await randomReader.CloseAsync();

            Console.WriteLine("Base chunk for comparison:");
            Console.WriteLine($"  Document: {basePath}");
            Console.WriteLine($"  Chunk Index: {baseIndex}");
            Console.WriteLine($"  Preview: {basePreview}...");
            Console.WriteLine();

            // Find most similar chunks
            Console.WriteLine("Finding most similar chunks...\n");

            var similarQuery = @"
                SELECT id, document_path, chunk_index,
                       LEFT(content, 100) as preview,
                       1 - (embedding <=> @baseVec) as similarity
                FROM chunks
                WHERE id != @baseId
                ORDER BY embedding <=> @baseVec
                LIMIT 5";

            await using var similarCmd = new NpgsqlCommand(similarQuery, conn);
            similarCmd.Parameters.AddWithValue("baseVec", baseEmbedding);
            similarCmd.Parameters.AddWithValue("baseId", baseId);

            await using var similarReader = await similarCmd.ExecuteReaderAsync();

            int count = 0;
            while (await similarReader.ReadAsync())
            {
                count++;
                var similarity = similarReader.GetFloat(4);

                Console.WriteLine($"#{count} - Similarity: {similarity:P2}");
                Console.WriteLine($"  Document: {similarReader.GetString(1)}");
                Console.WriteLine($"  Chunk Index: {similarReader.GetInt32(2)}");
                Console.WriteLine($"  Preview: {similarReader.GetString(3)}...");
                Console.WriteLine();
            }
        }

        // ============= Helper Methods =============
        private NpgsqlDataSource CreateDataSource()
        {
            var builder = new NpgsqlDataSourceBuilder(_connectionString);
            builder.UseVector();
            return builder.Build();
        }
    }
}