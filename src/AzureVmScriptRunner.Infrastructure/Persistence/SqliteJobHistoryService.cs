using System.Globalization;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.History;
using Microsoft.Data.Sqlite;

namespace AzureVmScriptRunner.Infrastructure.Persistence;

public sealed class SqliteJobHistoryService : IJobHistoryService
{
    private readonly SqliteDatabase _database;

    public SqliteJobHistoryService(SqliteDatabase database) => _database = database;

    public async Task RecordAsync(JobRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobRecords
                (Id, RequestId, RequestedBy, DisplayName, ExecutionType, Provider, VmName,
                 SubscriptionId, ResourceGroup, VmResourceId, ScriptContent, Status,
                 ExitCode, ExitCodeDescription, Output, Error, StartedAt, CompletedAt)
            VALUES
                ($id, $requestId, $requestedBy, $displayName, $executionType, $provider, $vmName,
                 $subscriptionId, $resourceGroup, $vmResourceId, $scriptContent, $status,
                 $exitCode, $exitCodeDescription, $output, $error, $startedAt, $completedAt)
            """;

        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$requestId", record.RequestId.ToString());
        command.Parameters.AddWithValue("$requestedBy", record.RequestedBy);
        command.Parameters.AddWithValue("$displayName", record.DisplayName);
        command.Parameters.AddWithValue("$executionType", record.ExecutionType.ToString());
        command.Parameters.AddWithValue("$provider", (object?)record.Provider?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$vmName", record.VmName);
        command.Parameters.AddWithValue("$subscriptionId", record.SubscriptionId);
        command.Parameters.AddWithValue("$resourceGroup", record.ResourceGroup);
        command.Parameters.AddWithValue("$vmResourceId", (object?)record.VmResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$scriptContent", (object?)record.ScriptContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$exitCode", (object?)record.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$exitCodeDescription", (object?)record.ExitCodeDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)record.Output ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", (object?)record.CompletedAt?.ToString("O") ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobRecord>> QueryAsync(
        JobHistoryQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            conditions.Add("(DisplayName LIKE $search OR VmName LIKE $search OR ScriptContent LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{query.SearchText}%");
        }

        if (!string.IsNullOrWhiteSpace(query.VmName))
        {
            conditions.Add("VmName = $vmName COLLATE NOCASE");
            command.Parameters.AddWithValue("$vmName", query.VmName);
        }

        if (!string.IsNullOrWhiteSpace(query.RequestedBy))
        {
            conditions.Add("RequestedBy = $requestedBy COLLATE NOCASE");
            command.Parameters.AddWithValue("$requestedBy", query.RequestedBy);
        }

        if (query.From is { } from)
        {
            conditions.Add("StartedAt >= $from");
            command.Parameters.AddWithValue("$from", from.ToString("O"));
        }

        if (query.To is { } to)
        {
            conditions.Add("StartedAt <= $to");
            command.Parameters.AddWithValue("$to", to.ToString("O"));
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        command.CommandText =
            $"SELECT * FROM JobRecords {where} ORDER BY StartedAt DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", query.MaxResults);

        var results = new List<JobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static JobRecord Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        RequestId = Guid.Parse(reader.GetString(reader.GetOrdinal("RequestId"))),
        RequestedBy = reader.GetString(reader.GetOrdinal("RequestedBy")),
        DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
        ExecutionType = Enum.Parse<ExecutionType>(reader.GetString(reader.GetOrdinal("ExecutionType"))),
        Provider = GetString(reader, "Provider") is { } p ? Enum.Parse<ExecutionProviderKind>(p) : null,
        VmName = reader.GetString(reader.GetOrdinal("VmName")),
        SubscriptionId = reader.GetString(reader.GetOrdinal("SubscriptionId")),
        ResourceGroup = reader.GetString(reader.GetOrdinal("ResourceGroup")),
        VmResourceId = GetString(reader, "VmResourceId"),
        ScriptContent = GetString(reader, "ScriptContent"),
        Status = Enum.Parse<VmExecutionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        ExitCode = reader.IsDBNull(reader.GetOrdinal("ExitCode"))
            ? null
            : (int)reader.GetInt64(reader.GetOrdinal("ExitCode")),
        ExitCodeDescription = GetString(reader, "ExitCodeDescription"),
        Output = GetString(reader, "Output"),
        Error = GetString(reader, "Error"),
        StartedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("StartedAt"))),
        CompletedAt = GetString(reader, "CompletedAt") is { } c ? ParseTimestamp(c) : null
    };

    private static string? GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
