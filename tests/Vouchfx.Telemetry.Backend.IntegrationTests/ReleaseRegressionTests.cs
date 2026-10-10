// CA1707: underscore-separated names are the xUnit naming convention.
#pragma warning disable CA1707
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vouchfx.Telemetry.Backend.Configuration;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>Regressions for configured bootstrap windows, lock order, and timing semantics.</summary>
[Collection("Postgres")]
public sealed class ReleaseRegressionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Bootstrap_UsesConfiguredRetentionAndPrecreateWindows()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync(retentionDays: 30, precreateDays: 3);
        Assert.Equal(33L, await database.CountDayPartitionsAsync());
        Assert.True(await database.PartitionExistsAsync(-30));
        Assert.False(await database.PartitionExistsAsync(-31));
        Assert.True(await database.PartitionExistsAsync(2));
        Assert.False(await database.PartitionExistsAsync(3));
    }

    [Fact]
    public async Task Bootstrap_AfterShorterRetention_DoesNotRecreateDroppedPartitions()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync(retentionDays: 90);
        await database.ExecuteAsync("SELECT drop_old_partitions(30)");
        Assert.Equal(37L, await database.CountDayPartitionsAsync());
        await database.BootstrapAsync(retentionDays: 30);
        Assert.Equal(37L, await database.CountDayPartitionsAsync());
        Assert.False(await database.PartitionExistsAsync(-31));
    }

    [Fact]
    public async Task Bootstrap_CreatingMissingPartition_DoesNotDeadlockWithForgetLockOrder()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync(retentionDays: 30);
        await database.DropFuturePartitionAsync();
        await using var drainer = await database.DataSource.OpenConnectionAsync();
        await using var tx = await drainer.BeginTransactionAsync();
        await using (var first = new NpgsqlCommand("DELETE FROM telemetry_event WHERE install_id = gen_random_uuid()", drainer, tx))
            await first.ExecuteNonQueryAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bootstrapTask = database.BootstrapAsync(retentionDays: 30, ct: cts.Token);
        var committed = false;
        try
        {
            await database.WaitUntilPartitionDdlBlockedAsync();
            // The main DDL must have committed before requesting the partition lock.
            // Otherwise this second statement closes a two-table deadlock cycle.
            await using (var second = new NpgsqlCommand("DELETE FROM ingest_batch WHERE install_id = gen_random_uuid()", drainer, tx))
                await second.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            committed = true;
            await bootstrapTask;
            Assert.True(await database.PartitionExistsAsync(2));
        }
        finally
        {
            if (!committed) await tx.RollbackAsync();
            // Await cleanup even when the competitor was a deadlock victim.
            _ = await Record.ExceptionAsync(() => bootstrapTask);
        }
    }

    [Fact]
    public async Task Bootstrap_PartitionLockWaitIsBounded_AndRetrySucceeds()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync(retentionDays: 30);
        await database.DropFuturePartitionAsync();
        await using var reader = await database.DataSource.OpenConnectionAsync();
        await using var tx = await reader.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT COUNT(*) FROM telemetry_event", reader, tx))
            await hold.ExecuteScalarAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.BootstrapAsync(retentionDays: 30, ct: cts.Token));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.InRange(watch.Elapsed.TotalSeconds, 4, 15);
        await tx.RollbackAsync();
        // Proves failed DDL rolls back and releases the session advisory lock.
        await database.BootstrapAsync(retentionDays: 30, ct: cts.Token);
        Assert.True(await database.PartitionExistsAsync(2));
    }

    [Fact]
    public async Task DailyRunView_TimingsUseOnlyKnownVersion2_CountsIncludeAllVersions()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync();
        var repo = new NpgsqlTelemetryRepository(database.DataSource, TimeProvider.System, NullLogger<NpgsqlTelemetryRepository>.Instance);
        var timestamp = DateTimeOffset.UtcNow;
        var events = new[]
        {
            TestData.MakeEvent(timestamp: timestamp, schemaVersion: 1) with { StartupMs = 0, TimeToFirstTestMs = 0 },
            TestData.MakeEvent(timestamp: timestamp, schemaVersion: 2) with { StartupMs = 100, TimeToFirstTestMs = 200 },
            TestData.MakeEvent(timestamp: timestamp, schemaVersion: 3) with { StartupMs = 10_000, TimeToFirstTestMs = 20_000 },
        };
        Assert.Equal(3, await repo.IngestAsync(events, TestData.MakeIdempotencyKey(), CancellationToken.None));

        await using var conn = await database.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT events, runs, scenarios, distinct_installs, startup_ms_p50, startup_ms_p95, " +
            "time_to_first_test_ms_p50, time_to_first_test_ms_p95 FROM v_run_daily", conn);
        await using var row = await cmd.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        for (var i = 0; i < 4; i++) Assert.Equal(3L, row.GetInt64(i));
        Assert.Equal(100d, row.GetDouble(4));
        Assert.Equal(100d, row.GetDouble(5));
        Assert.Equal(200d, row.GetDouble(6));
        Assert.Equal(200d, row.GetDouble(7));
        Assert.False(await row.ReadAsync());
    }

    [Fact]
    public async Task DailyRunView_NoVersion2Events_TimingPercentilesAreNull()
    {
        await using var database = await IsolatedDatabase.CreateAsync(fixture);
        await database.BootstrapAsync();
        var repo = new NpgsqlTelemetryRepository(database.DataSource, TimeProvider.System, NullLogger<NpgsqlTelemetryRepository>.Instance);
        await repo.IngestAsync([TestData.MakeEvent(schemaVersion: 1)], TestData.MakeIdempotencyKey(), CancellationToken.None);
        await using var conn = await database.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT events, startup_ms_p50, startup_ms_p95, time_to_first_test_ms_p50, time_to_first_test_ms_p95 FROM v_run_daily", conn);
        await using var row = await cmd.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1L, row.GetInt64(0));
        for (var i = 1; i <= 4; i++) Assert.True(row.IsDBNull(i));
    }

    private sealed class IsolatedDatabase(PostgresFixture shared, string schema, NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;

        public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture shared)
        {
            // A private schema prevents destructive partition tests affecting other cases.
            var schema = "release_" + Guid.NewGuid().ToString("N");
            await using var conn = await shared.DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn);
            await cmd.ExecuteNonQueryAsync();
            var builder = new NpgsqlConnectionStringBuilder(shared.ConnectionString) { SearchPath = schema };
            return new IsolatedDatabase(shared, schema, NpgsqlDataSourceFactory.Build(builder.ConnectionString));
        }

        public Task BootstrapAsync(int retentionDays = 90, int precreateDays = 7, CancellationToken ct = default) =>
            new DbBootstrapper(DataSource, NullLogger<DbBootstrapper>.Instance,
                Options.Create(new TelemetryOptions { RetentionDays = retentionDays, PrecreateDays = precreateDays })).BootstrapAsync(ct);

        public async Task ExecuteAsync(string sql)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> CountDayPartitionsAsync()
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_inherits WHERE inhparent = 'telemetry_event'::regclass " +
                "AND inhrelid <> (SELECT partdefid FROM pg_partitioned_table WHERE partrelid = 'telemetry_event'::regclass)", conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<bool> PartitionExistsAsync(int offsetDays)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('telemetry_event_' || to_char(current_date + $1::int, 'YYYYMMDD')) IS NOT NULL", conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = offsetDays, DataTypeName = "int4" });
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }

        public Task DropFuturePartitionAsync() => ExecuteAsync(
            "DO $$ BEGIN EXECUTE 'DROP TABLE telemetry_event_' || to_char(current_date + 2, 'YYYYMMDD'); END $$;");

        public async Task WaitUntilPartitionDdlBlockedAsync()
        {
            // Observe the exact requested parent-table lock, never rely on a sleep to
            // claim the adversarial interleaving occurred. Bounds fail a vacuous test.
            for (var i = 0; i < 160; i++)
            {
                await using var conn = await DataSource.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE NOT granted AND mode = 'AccessExclusiveLock' " +
                    "AND relation = 'telemetry_event'::regclass)", conn);
                if ((bool)(await cmd.ExecuteScalarAsync())!) return;
                await Task.Delay(25);
            }

            Assert.Fail("Bootstrap never requested the missing partition's conflicting parent lock.");
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using var conn = await shared.DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
