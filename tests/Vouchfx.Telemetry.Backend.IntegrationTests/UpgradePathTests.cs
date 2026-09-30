// CA1707: underscore test names are the xUnit convention.
#pragma warning disable CA1707

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Proves the ONLY path that adds <c>skipped_event_lines</c> to a pre-issue-#30 deployment:
/// re-running <see cref="DbBootstrapper"/> over a database that was already bootstrapped
/// before the column existed. <see cref="PostgresFixture"/> bootstraps a brand-new,
/// empty database, where <c>CREATE TABLE IF NOT EXISTS</c> already declares the column —
/// that alone never exercises the upgrade branch (the catalog-guarded <c>DO $guard$</c>
/// block in <c>bootstrap.sql</c>), so this test simulates the pre-#30 shape explicitly:
/// bootstrap, then drop the column, then re-bootstrap.
/// </summary>
/// <remarks>
/// Lives in its own "PostgresUpgrade" collection (its own <see cref="PostgresFixture"/>
/// instance / Testcontainers database), mirroring the isolation the "ProductionBoot"
/// collection already uses — dropping a column from the shared "Postgres" collection's
/// database while its tests run would corrupt them.
/// </remarks>
[Collection("PostgresUpgrade")]
public sealed class UpgradePathTests(PostgresFixture fixture)
{
    /// <summary>
    /// Deleting the schema-evolution step (the guarded <c>ALTER</c> in
    /// <c>bootstrap-schema-evolution.sql</c>) leaves every other integration test green,
    /// because <see cref="PostgresFixture"/> bootstraps a brand-new, empty database where
    /// <c>CREATE TABLE IF NOT EXISTS</c> already declares the column — none of those tests
    /// simulates an existing pre-issue-#30 database. This test closes that gap end to end:
    /// drop the column (proving it is gone from the parent AND every partition), seed
    /// legacy rows in both a day partition and the DEFAULT partition, re-bootstrap, and
    /// assert the column is back everywhere, correctly constrained, the legacy rows read
    /// 0, and a fresh version-2 ingest stores its real value.
    /// </summary>
    [Fact]
    public async Task Bootstrap_UpgradesPreExistingDatabaseMissingSkippedEventLines()
    {
        // 1. Simulate a pre-#30 database: drop the column from the already-bootstrapped
        //    parent. PostgreSQL propagates a parent DROP COLUMN to every partition.
        await using (var conn = await fixture.DataSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "ALTER TABLE telemetry_event DROP COLUMN skipped_event_lines", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Equal(0L, await CountNonDroppedColumnAsync("telemetry_event"));
        var (totalAfterDrop, withColumnAfterDrop) = await CountChildPartitionsWithColumnAsync();
        Assert.True(totalAfterDrop > 0, "expected at least one child partition to exist before the drop");
        Assert.Equal(0L, withColumnAfterDrop); // dropped from every partition, not just the parent

        // 2. Seed legacy rows through a 20-column insert (the pre-#30 shape): one lands in
        //    an explicit day partition (already pre-created by the fixture's initial
        //    bootstrap), one lands in the DEFAULT catch-all (outside the pre-created
        //    +7-day window).
        var dayInstallId = Guid.NewGuid();
        var defaultInstallId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;
        await InsertLegacyRowAsync(dayInstallId, today.AddHours(12));
        await InsertLegacyRowAsync(defaultInstallId, today.AddDays(200).AddHours(12));

        Assert.Equal(1L, await CountInPartitionAsync(dayInstallId, $"telemetry_event_{today:yyyyMMdd}"));
        Assert.Equal(1L, await CountInPartitionAsync(defaultInstallId, "telemetry_event_default"));

        // 3. Run the REAL DbBootstrapper again — production's only upgrade path.
        var bootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
        await bootstrapper.BootstrapAsync(CancellationToken.None);

        // 4a. The column exists again on the parent AND on every partition (count child
        //     partitions from pg_inherits against pg_attribute — none silently skipped).
        Assert.Equal(1L, await CountNonDroppedColumnAsync("telemetry_event"));
        var (totalAfterUpgrade, withColumnAfterUpgrade) = await CountChildPartitionsWithColumnAsync();
        Assert.True(totalAfterUpgrade > 0, "expected at least one child partition to exist after the upgrade");
        Assert.Equal(totalAfterUpgrade, withColumnAfterUpgrade);

        // 4b. NOT NULL with EXACTLY default 0 — checked on the parent AND on every child
        //     partition individually (a default of 10 or 100 would also satisfy a loose
        //     Contains("0") check; pg_get_expr compares the exact default expression).
        Assert.True(await ParentColumnIsNotNullWithZeroDefaultAsync());
        var (totalForConstraint, childrenWithConstraint) = await CountChildPartitionsWithConstraintAsync();
        Assert.True(totalForConstraint > 0, "expected at least one child partition to check constraints on");
        Assert.Equal(totalForConstraint, childrenWithConstraint);

        // 4c. The seeded legacy rows read back 0 (the column's default, applied
        //     retroactively by ADD COLUMN ... DEFAULT 0 to every pre-existing row).
        Assert.Equal(0, await GetSkippedEventLinesAsync(dayInstallId));
        Assert.Equal(0, await GetSkippedEventLinesAsync(defaultInstallId));

        // 4d. A version-2 event ingested through the repository after the upgrade stores
        //     its real value (the upgraded column is usable, not just present).
        var repo = new NpgsqlTelemetryRepository(
            fixture.DataSource, TimeProvider.System, NullLogger<NpgsqlTelemetryRepository>.Instance);
        var v2InstallId = Guid.NewGuid();
        var v2Event = TestData.MakeEvent(installId: v2InstallId, schemaVersion: 2, skippedEventLines: 9);
        await repo.IngestAsync(new[] { v2Event }, TestData.MakeIdempotencyKey(), CancellationToken.None);
        Assert.Equal(9, await GetSkippedEventLinesAsync(v2InstallId));
    }

    /// <summary>
    /// A warm start (column already present) takes only ACCESS SHARE on
    /// <c>telemetry_event</c>, never attempting the guarded ALTER. A holder transaction
    /// takes ACCESS SHARE via a plain SELECT and keeps it open; a concurrent bootstrap run
    /// — under a bounded cancellation, so an unconditional ALTER's ACCESS EXCLUSIVE wait
    /// shows up as a timeout here rather than hanging the test suite — must still
    /// complete, because the guard means no ACCESS EXCLUSIVE lock is ever requested when
    /// the column already exists.
    /// </summary>
    /// <remarks>
    /// Runs an untimed bootstrap WARM before taking the reader's lock: <c>bootstrap.sql</c>'s
    /// final step, <c>ensure_partitions(current_date - 90, current_date + 7)</c>, pre-creates
    /// any partition due for "today". Without this warm-up, a UTC date rollover between the
    /// fixture's initial bootstrap and this test's timed run could make the timed run's own
    /// <c>ensure_partition</c> create a brand-new day partition WHILE the ACCESS SHARE
    /// holder is open — partition creation takes a stronger lock than ACCESS SHARE
    /// regardless of the skipped_event_lines guard, which would flake this test on exactly
    /// that boundary and has nothing to do with the guard this test exists to prove.
    /// </remarks>
    [Fact]
    public async Task Bootstrap_WarmStart_DoesNotBlockOnConcurrentAccessShareHolder()
    {
        var warmBootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
        await warmBootstrapper.BootstrapAsync(CancellationToken.None);

        await using var holderConn = await fixture.DataSource.OpenConnectionAsync();
        await using var holderTx = await holderConn.BeginTransactionAsync();
        await using (var selectCmd = new NpgsqlCommand("SELECT COUNT(*) FROM telemetry_event", holderConn, holderTx))
        {
            await selectCmd.ExecuteScalarAsync(); // takes and keeps ACCESS SHARE for the transaction's life
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
            await bootstrapper.BootstrapAsync(cts.Token); // must complete well within 5s, not throw
        }
        finally
        {
            await holderTx.RollbackAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<long> CountNonDroppedColumnAsync(string relationName)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_attribute WHERE attrelid = $1::regclass " +
            "AND attname = 'skipped_event_lines' AND NOT attisdropped", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = relationName, DataTypeName = "text" });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // Counts direct child partitions of telemetry_event (pg_inherits) against how many of
    // them have a non-dropped skipped_event_lines column (pg_attribute) — a single query so
    // "every partition, none skipped" is one equality assertion, not a per-partition loop.
    private async Task<(long TotalChildren, long ChildrenWithColumn)> CountChildPartitionsWithColumnAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM pg_inherits WHERE inhparent = 'telemetry_event'::regclass),
                (SELECT COUNT(*) FROM pg_inherits i
                   JOIN pg_attribute a ON a.attrelid = i.inhrelid
                  WHERE i.inhparent = 'telemetry_event'::regclass
                    AND a.attname = 'skipped_event_lines' AND NOT a.attisdropped)
            """,
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // Exact check (not a substring match, which "10" or "100" would also satisfy) that the
    // PARENT's column is NOT NULL with a default expression that is exactly 0.
    // pg_get_expr(adbin, adrelid) renders the default's parse tree back to its exact SQL
    // text — comparing it to the literal '0' string.
    private async Task<bool> ParentColumnIsNotNullWithZeroDefaultAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.attnotnull AND pg_get_expr(d.adbin, d.adrelid) = '0'
            FROM pg_attribute a
            JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = 'telemetry_event'::regclass
              AND a.attname = 'skipped_event_lines'
              AND NOT a.attisdropped
            """,
            conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is bool b && b;
    }

    // Same exact NOT NULL + default-0 check as ParentColumnIsNotNullWithZeroDefaultAsync,
    // but per CHILD partition (ALTER TABLE ADD COLUMN on the parent gives each child its
    // own pg_attribute/pg_attrdef rows, not just the parent's) — one query so "every
    // partition satisfies both constraints" is one equality assertion, not a per-partition
    // loop.
    private async Task<(long TotalChildren, long ChildrenNotNullWithZeroDefault)> CountChildPartitionsWithConstraintAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM pg_inherits WHERE inhparent = 'telemetry_event'::regclass),
                (SELECT COUNT(*) FROM pg_inherits i
                   JOIN pg_attribute a ON a.attrelid = i.inhrelid
                   JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                  WHERE i.inhparent = 'telemetry_event'::regclass
                    AND a.attname = 'skipped_event_lines'
                    AND NOT a.attisdropped
                    AND a.attnotnull
                    AND pg_get_expr(d.adbin, d.adrelid) = '0')
            """,
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<int> GetSkippedEventLinesAsync(Guid installId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT skipped_event_lines FROM telemetry_event WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountInPartitionAsync(Guid installId, string partitionName)
    {
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

    // Inserts a row using the pre-issue-#30, 20-column shape (no skipped_event_lines) —
    // simulates data written before the column existed, independent of the current DTO.
    private async Task InsertLegacyRowAsync(Guid installId, DateTime eventTimestampUtc)
    {
        const string Sql =
            """
            INSERT INTO telemetry_event (
                install_id, event_timestamp, schema_version,
                tool_version, engine_version, dotnet_version,
                run_count, scenario_count,
                step_pass, step_fail, step_env_error, step_inconclusive,
                scenario_pass, scenario_fail, scenario_env_error, scenario_inconclusive,
                step_families, step_providers,
                startup_ms, time_to_first_test_ms
            ) VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                $11, $12, $13, $14, $15, $16, $17::jsonb, $18::jsonb, $19, $20
            )
            """;

        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = DateTime.SpecifyKind(eventTimestampUtc, DateTimeKind.Utc),
            DataTypeName = "timestamptz",
        });
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1, DataTypeName = "int4" });          // schema_version
        cmd.Parameters.Add(new NpgsqlParameter { Value = "1.0.0", DataTypeName = "text" });     // tool_version
        cmd.Parameters.Add(new NpgsqlParameter { Value = "1.0.0", DataTypeName = "text" });     // engine_version
        cmd.Parameters.Add(new NpgsqlParameter { Value = ".NET 8.0", DataTypeName = "text" });  // dotnet_version
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1, DataTypeName = "int4" });           // run_count
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1, DataTypeName = "int4" });           // scenario_count
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1, DataTypeName = "int4" });           // step_pass
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // step_fail
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // step_env_error
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // step_inconclusive
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1, DataTypeName = "int4" });           // scenario_pass
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // scenario_fail
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // scenario_env_error
        cmd.Parameters.Add(new NpgsqlParameter { Value = 0, DataTypeName = "int4" });           // scenario_inconclusive
        cmd.Parameters.Add(new NpgsqlParameter { Value = "{\"http\":1}", DataTypeName = "jsonb" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = "{\"http.rest\":1}", DataTypeName = "jsonb" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = 100L, DataTypeName = "int8" });        // startup_ms
        cmd.Parameters.Add(new NpgsqlParameter { Value = 200L, DataTypeName = "int8" });        // time_to_first_test_ms

        await cmd.ExecuteNonQueryAsync();
    }
}
