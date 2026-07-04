using Microsoft.Data.Sqlite;

namespace AzureVmScriptRunner.Infrastructure.Persistence;

/// <summary>
/// Local per-administrator store (job history + saved tasks). Deliberately behind the
/// Application interfaces so a shared multi-admin store can replace it without UI or
/// orchestration changes.
/// </summary>
public sealed class SqliteDatabase
{
    public string ConnectionString { get; }

    public SqliteDatabase(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzureVmScriptRunner", "avsr.db");

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS JobRecords (
                Id TEXT PRIMARY KEY,
                RequestId TEXT NOT NULL,
                RequestedBy TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                ExecutionType TEXT NOT NULL,
                Provider TEXT NULL,
                VmName TEXT NOT NULL,
                SubscriptionId TEXT NOT NULL,
                ResourceGroup TEXT NOT NULL,
                VmResourceId TEXT NULL,
                ScriptContent TEXT NULL,
                Status TEXT NOT NULL,
                ExitCode INTEGER NULL,
                ExitCodeDescription TEXT NULL,
                Output TEXT NULL,
                Error TEXT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_JobRecords_StartedAt ON JobRecords (StartedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_JobRecords_VmName ON JobRecords (VmName);

            CREATE TABLE IF NOT EXISTS SavedTasks (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                Category TEXT NULL,
                PayloadJson TEXT NOT NULL,
                OptionsJson TEXT NOT NULL,
                IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NULL,
                ModifiedBy TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
