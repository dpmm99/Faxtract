using Faxtract.Models;
using Microsoft.Data.Sqlite;

namespace Faxtract.Services;

public class StorageService(IConfiguration configuration)
{
    private string DbPath => Path.Combine(AppContext.BaseDirectory, "out", configuration.GetSection("LLamaConfig").GetValue("DatabaseFile", "flashcards.db")!);

    private void InitializeDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        if (File.Exists(DbPath))
        {
            // If the database already exists, we assume the schema is correct
            return;
        }

        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // Create TextChunks table if not exists
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS TextChunks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Content TEXT NOT NULL,
                StartPosition INTEGER NOT NULL,
                EndPosition INTEGER NOT NULL,
                FileId TEXT NOT NULL,
                ExtraContext TEXT
            )
            """;
            command.ExecuteNonQuery();
        }

        // Create FlashCards table if not exists
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS FlashCards (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Question TEXT NOT NULL,
                Answer TEXT NOT NULL,
                OriginId INTEGER NOT NULL,
                FOREIGN KEY (OriginId) REFERENCES TextChunks(Id)
            )
            """;
            command.ExecuteNonQuery();
        }
    }

    public async Task SaveAsync(List<FlashCard> flashCards)
    {
        if (flashCards.Count == 0)
            return;

        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        try
        {
            // Insert FlashCards
            foreach (var card in flashCards)
            {
                // We assume TextChunks have already been inserted and have valid IDs
                await using var command = connection.CreateCommand();
                command.CommandText = """
                INSERT INTO FlashCards (Question, Answer, OriginId)
                VALUES (@Question, @Answer, @OriginId);
                SELECT last_insert_rowid();
                """;

                command.Parameters.AddWithValue("@Question", card.Question);
                command.Parameters.AddWithValue("@Answer", card.Answer);
                command.Parameters.AddWithValue("@OriginId", card.Origin.Id);

                // Set the generated ID back to the FlashCard
                card.Id = Convert.ToInt32(await command.ExecuteScalarAsync());
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task SaveAsync(List<TextChunk> chunks)
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var chunk in chunks)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO TextChunks (Content, StartPosition, EndPosition, FileId, ExtraContext)
                    VALUES (@Content, @StartPosition, @EndPosition, @FileId, @ExtraContext);
                    SELECT last_insert_rowid();
                    """;

                command.Parameters.AddWithValue("@Content", chunk.Content);
                command.Parameters.AddWithValue("@StartPosition", chunk.StartPosition);
                command.Parameters.AddWithValue("@EndPosition", chunk.EndPosition);
                command.Parameters.AddWithValue("@FileId", chunk.FileId);
                command.Parameters.AddWithValue("@ExtraContext", chunk.ExtraContext ?? (object)DBNull.Value);

                // Set the generated ID back to the TextChunk
                chunk.Id = Convert.ToInt32(await command.ExecuteScalarAsync());
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<FlashCardChartData>> GetFlashCardChartDataAsync()
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        var result = new List<FlashCardChartData>();

        // Query to get text chunks and their flash card counts with file information
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tc.FileId, tc.Id AS ChunkId, COUNT(fc.Id) AS FlashCardCount
            FROM TextChunks tc
            LEFT JOIN FlashCards fc ON tc.Id = fc.OriginId
            GROUP BY tc.FileId, tc.Id
            ORDER BY tc.FileId, tc.StartPosition
            """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new FlashCardChartData
            {
                FileId = reader.GetString(0),
                ChunkId = reader.GetInt32(1),
                FlashCardCount = reader.GetInt32(2)
            });
        }

        return result;
    }

    public async Task<FlashCardDetails> GetFlashCardDetailsAsync(int chunkId)
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        // Get the TextChunk information
        TextChunk? chunk = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            SELECT Id, Content, StartPosition, EndPosition, FileId, ExtraContext
            FROM TextChunks
            WHERE Id = @ChunkId
            """;
            command.Parameters.AddWithValue("@ChunkId", chunkId);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                chunk = new TextChunk(
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    !reader.IsDBNull(5) ? reader.GetString(5) : null
                )
                {
                    Id = reader.GetInt32(0)
                };
            }
        }

        if (chunk == null)
        {
            return new FlashCardDetails
            {
                Chunk = null,
                FlashCards = []
            };
        }

        // Get all flash cards associated with this chunk
        var flashCards = new List<FlashCard>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            SELECT Id, Question, Answer
            FROM FlashCards
            WHERE OriginId = @ChunkId
            """;
            command.Parameters.AddWithValue("@ChunkId", chunkId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                flashCards.Add(new FlashCard
                {
                    Id = reader.GetInt32(0),
                    Question = reader.GetString(1),
                    Answer = reader.GetString(2),
                    Origin = chunk,
                    OriginId = chunk.Id
                });
            }
        }

        return new FlashCardDetails
        {
            Chunk = chunk,
            FlashCards = flashCards
        };
    }

    public async Task DeleteFlashCardAsync(int flashCardId)
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        try
        {
            // Delete all flash cards associated with the chunk
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM FlashCards WHERE Id = @FlashCardId";
            command.Parameters.AddWithValue("@FlashCardId", flashCardId);
            await command.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteChunkAsync(int chunkId)
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        try
        {
            // Delete all flash cards for these chunks
            await using var deleteFlashCardsCommand = connection.CreateCommand();
            deleteFlashCardsCommand.CommandText = "DELETE FROM FlashCards WHERE OriginId = @ChunkId";
            deleteFlashCardsCommand.Parameters.AddWithValue("@ChunkId", chunkId);
            await deleteFlashCardsCommand.ExecuteNonQueryAsync();

            // Delete the chunk
            await using var deleteChunkCommand = connection.CreateCommand();
            deleteChunkCommand.CommandText = "DELETE FROM TextChunks WHERE Id = @ChunkId";
            deleteChunkCommand.Parameters.AddWithValue("@ChunkId", chunkId);

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<TextChunk?> GetChunkAsync(int chunkId)
    {
        InitializeDatabase();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content, StartPosition, EndPosition, FileId, ExtraContext FROM TextChunks WHERE Id = @ChunkId";
        command.Parameters.AddWithValue("@ChunkId", chunkId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new TextChunk(
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                !reader.IsDBNull(5) ? reader.GetString(5) : null
            )
            {
                Id = reader.GetInt32(0)
            };
        }

        return null;
    }

    public class FlashCardDetails
    {
        public TextChunk? Chunk { get; set; }
        public List<FlashCard> FlashCards { get; set; } = [];
    }

    public class FlashCardChartData
    {
        public string FileId { get; set; } = string.Empty;
        public int ChunkId { get; set; }
        public int FlashCardCount { get; set; }
    }
}
