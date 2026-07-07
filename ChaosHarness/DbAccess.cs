using Npgsql;

namespace ChaosHarness;

internal sealed class DbAccess
{
    private readonly string _connectionString;

    public DbAccess(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task TruncateAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE \"Tasks\", \"Successes\", \"Conflicts\", \"DLQ\" RESTART IDENTITY CASCADE",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TerminalCounts> GetTerminalCountsAsync(IReadOnlyCollection<Guid> taskIds)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var ids = taskIds.ToArray();

        await using var cmd = new NpgsqlCommand(@"
            SELECT
              (SELECT COUNT(*) FROM ""Successes"" WHERE ""TaskId"" = ANY(@ids)),
              (SELECT COUNT(*) FROM ""DLQ""       WHERE ""TaskId"" = ANY(@ids)),
              (SELECT COUNT(*) FROM unnest(@ids) AS t(id)
                 WHERE NOT EXISTS (SELECT 1 FROM ""Successes"" WHERE ""TaskId"" = t.id)
                   AND NOT EXISTS (SELECT 1 FROM ""DLQ""       WHERE ""TaskId"" = t.id))",
            conn);
        cmd.Parameters.AddWithValue("ids", ids);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new TerminalCounts(
            Successes: reader.GetInt64(0),
            DeadLettered: reader.GetInt64(1),
            Stuck: reader.GetInt64(2));
    }

    public async Task<long> GetLeaderTokenAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"Token\" FROM \"Leader\" WHERE \"Id\" = 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : (long)result;
    }

    public async Task<string?> GetLeaderContainerAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"ContainerId\" FROM \"Leader\" WHERE \"Id\" = 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    public async Task<long> CountDuplicateSuccessesAsync(IReadOnlyCollection<Guid> taskIds)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM (
                SELECT ""TaskId"" FROM ""Successes""
                WHERE ""TaskId"" = ANY(@ids)
                GROUP BY ""TaskId"" HAVING COUNT(*) > 1
            ) dups",
            conn);
        cmd.Parameters.AddWithValue("ids", taskIds.ToArray());
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}

internal readonly record struct TerminalCounts(long Successes, long DeadLettered, long Stuck)
{
    public bool AllSettled => Stuck == 0;
}
