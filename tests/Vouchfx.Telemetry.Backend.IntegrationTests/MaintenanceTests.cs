// CA1707: underscore test names are the xUnit convention.
#pragma warning disable CA1707

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouchfx.Telemetry.Backend.Background;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Integration tests for partition routing, purge (<see cref="PartitionManager"/>),
/// and advisory-lock exclusion (<see cref="PgAdvisoryLock"/>).
/// </summary>
[Collection("Postgres")]
public sealed class MaintenanceTests(PostgresFixture fixture)
{
    private NpgsqlTelemetryRepository CreateRepo() =>
        new(fixture.DataSource, TimeProvider.System, NullLogger<NpgsqlTelemetryRepository>.Instance);

    // ── Partition routing ─────────────────────────────────────────────────────

    /// <summary>An event timestamped today must land in the daily partition, not the DEFAULT catch-all.</summary>
    [Fact]
    public async Task IngestAsync_TodayEvent_LandsInDailyPartitionNotDefault()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();
        var today = DateTimeOffset.UtcNow;
        var ev = TestData.MakeEvent(installId: installId, timestamp: today);

        await repo.IngestAsync(new[] { ev }, TestData.MakeIdempotencyKey(), CancellationToken.None);

        // Check the row is in the today partition (not the DEFAULT partition)
        var todayPartition = $"telemetry_event_{today.UtcDateTime:yyyyMMdd}";
        var inToday = await CountInPartitionAsync(installId, todayPartition);
        var inDefault = await CountInPartitionAsync(installId, "telemetry_event_default");

        Assert.Equal(1L, inToday);
        Assert.Equal(0L, inDefault);
    }

    // ── Purge ─────────────────────────────────────────────────────────────────

    /// <summary>drop_old_partitions must remove a partition whose upper bound is at or before the retention cutoff.</summary>
    [Fact]
    public async Task DropOldPartitions_RemovesPartitionOlderThanRetention()
    {
        // 1. Create a partition 91 days ago and insert a row into it directly.
        var oldDate = DateTime.UtcNow.Date.AddDays(-91);
        await EnsurePartitionAsync(oldDate);

        var installId = Guid.NewGuid();
        await InsertDirectAsync(installId, oldDate);

        // Verify the row exists before purge
        var countBefore = await CountEventRowsAsync(installId);
        Assert.Equal(1L, countBefore);

        // 2. Drop partitions with retention=90 → the 91-day-old partition must be dropped.
        var pm = new PartitionManager(fixture.DataSource, NullLogger<PartitionManager>.Instance);
        await pm.DropOldPartitionsAsync(retentionDays: 90, CancellationToken.None);

        // 3. The row is gone (partition was dropped, which removed all data).
        var countAfter = await CountEventRowsAsync(installId);
        Assert.Equal(0L, countAfter);
    }

    // ── Advisory lock ─────────────────────────────────────────────────────────

    /// <summary>A second session must not acquire the advisory lock while the first session holds it.</summary>
    [Fact]
    public async Task AdvisoryLock_SecondAcquire_ReturnsFalse_WhileFirstHeld()
    {
        // Two separate data sources → two sessions → only one can hold the lock.
        await using var ds1 = NpgsqlDataSourceFactory.Build(fixture.ConnectionString);
        await using var ds2 = NpgsqlDataSourceFactory.Build(fixture.ConnectionString);

        var lock1 = new PgAdvisoryLock(ds1);
        var lock2 = new PgAdvisoryLock(ds2);
        try
        {
            var acquired1 = await lock1.TryAcquireAsync(CancellationToken.None);
            var acquired2 = await lock2.TryAcquireAsync(CancellationToken.None);

            Assert.True(acquired1);
            Assert.False(acquired2); // lock1 holds it
        }
        finally
        {
            await lock1.DisposeAsync();
            await lock2.DisposeAsync();
        }
    }

    /// <summary>After releasing the advisory lock it must be acquirable again.</summary>
    [Fact]
    public async Task AdvisoryLock_AfterRelease_CanBeAcquiredAgain()
    {
        await using var ds = NpgsqlDataSourceFactory.Build(fixture.ConnectionString);

        var lock1 = new PgAdvisoryLock(ds);
        Assert.True(await lock1.TryAcquireAsync(CancellationToken.None));
        await lock1.DisposeAsync(); // release

        var lock2 = new PgAdvisoryLock(ds);
        Assert.True(await lock2.TryAcquireAsync(CancellationToken.None));
        await lock2.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<long> CountEventRowsAsync(Guid installId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM telemetry_event WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountInPartitionAsync(Guid installId, string partitionName)
    {
        // Query the specific child partition directly (bypasses partitioned parent).
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace " +
            $"WHERE n.nspname='public' AND c.relname='{partitionName}')",
            conn);
        var exists = (bool)(await checkCmd.ExecuteScalarAsync())!;
        if (!exists)
        {
            return 0L;
        }

        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {partitionName} WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task EnsurePartitionAsync(DateTime date)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT ensure_partition($1::date)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = date, DataTypeName = "date" });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDirectAsync(Guid installId, DateTime date)
    {
        // Insert directly with a specific (old) event_timestamp to land in the old partition.
        var ts = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc);
        var repo = CreateRepo();
        var ev = TestData.MakeEvent(
            installId: installId,
            timestamp: new DateTimeOffset(ts));
        await repo.IngestAsync(new[] { ev }, TestData.MakeIdempotencyKey(), CancellationToken.None);
    }
}
