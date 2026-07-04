using System.Globalization;
using System.Text.Json;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace AzureVmScriptRunner.Infrastructure.Persistence;

public sealed class SqliteSavedTaskRepository : ISavedTaskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDatabase _database;

    public SqliteSavedTaskRepository(SqliteDatabase database) => _database = database;

    public async Task<SavedTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SavedTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<SavedTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SavedTasks ORDER BY Name";

        var results = new List<SavedTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task SaveAsync(SavedTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SavedTasks
                (Id, Name, Description, Category, PayloadJson, OptionsJson, IsBuiltIn, CreatedAt, ModifiedAt, ModifiedBy)
            VALUES
                ($id, $name, $description, $category, $payload, $options, $isBuiltIn, $createdAt, $modifiedAt, $modifiedBy)
            ON CONFLICT (Id) DO UPDATE SET
                Name = $name, Description = $description, Category = $category,
                PayloadJson = $payload, OptionsJson = $options,
                ModifiedAt = $modifiedAt, ModifiedBy = $modifiedBy
            """;

        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$name", task.Name);
        command.Parameters.AddWithValue("$description", (object?)task.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)task.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task.Payload, JsonOptions));
        command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(task.Options, JsonOptions));
        command.Parameters.AddWithValue("$isBuiltIn", task.IsBuiltIn ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$modifiedAt", (object?)task.ModifiedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$modifiedBy", (object?)task.ModifiedBy ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SavedTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SavedTask Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        Description = GetString(reader, "Description"),
        Category = GetString(reader, "Category"),
        Payload = JsonSerializer.Deserialize<ExecutionPayload>(
            reader.GetString(reader.GetOrdinal("PayloadJson")), JsonOptions)!,
        Options = JsonSerializer.Deserialize<ExecutionOptions>(
            reader.GetString(reader.GetOrdinal("OptionsJson")), JsonOptions)!,
        IsBuiltIn = reader.GetInt64(reader.GetOrdinal("IsBuiltIn")) == 1,
        CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        ModifiedAt = GetString(reader, "ModifiedAt") is { } m ? ParseTimestamp(m) : null,
        ModifiedBy = GetString(reader, "ModifiedBy")
    };

    private static string? GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
