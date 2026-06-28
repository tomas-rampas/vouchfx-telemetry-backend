using Npgsql;

namespace Vouchfx.Telemetry.Backend.Background;

/// <summary>Manages daily range-partitions on <c>telemetry_event</c>.</summary>
internal sealed partial class PartitionManager(
    NpgsqlDataSource dataSource,
    ILogger<PartitionManager> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensured partitions {Days} days ahead.")]
    private static partial void LogPartitionsEnsured(ILogger<PartitionManager> logger, int days);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped partitions older than {Days} days.")]
    private static partial void LogPartitionsDropped(ILogger<PartitionManager> logger, int days);

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Rows} rows from default partition.")]
    private static partial void LogDefaultSwept(ILogger<PartitionManager> logger, long rows);

    /// <summary>Calls <c>ensure_partitions(current_date, current_date + precreateDays)</c>.</summary>
    public async Task EnsurePartitionsAheadAsync(int precreateDays, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT ensure_partitions(current_date, current_date + $1::int)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = precreateDays, DataTypeName = "int4" });
        await cmd.ExecuteNonQueryAsync(ct);
        LogPartitionsEnsured(logger, precreateDays);
    }

    /// <summary>Calls <c>drop_old_partitions(retentionDays)</c>.</summary>
    public async Task DropOldPartitionsAsync(int retentionDays, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT drop_old_partitions($1::int)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = retentionDays, DataTypeName = "int4" });
        await cmd.ExecuteNonQueryAsync(ct);
        LogPartitionsDropped(logger, retentionDays);
    }

    /// <summary>Calls <c>sweep_default(retentionDays)</c> and logs the row count.</summary>
    public async Task SweepDefaultAsync(int retentionDays, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT sweep_default($1::int)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = retentionDays, DataTypeName = "int4" });
        var deleted = (long)(await cmd.ExecuteScalarAsync(ct))!;
        LogDefaultSwept(logger, deleted);
    }
}
