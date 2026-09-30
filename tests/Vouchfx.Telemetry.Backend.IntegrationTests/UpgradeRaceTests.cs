// CA1707: underscore test names are the xUnit convention.
#pragma warning disable CA1707

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Regression test (issue #30): an upgrade start (the skipped_event_lines schema-evolution
/// step) must not deadlock (Postgres 40P01) against a concurrent ingest or forget-drain
/// transaction, regardless of which table each one locks first.
/// </summary>
/// <remarks>
/// <para>
/// The competitor's FIRST statement runs and its transaction is held open (not committed)
/// BEFORE the bootstrap is even started. The bootstrap is then started, and an explicit
/// barrier polls <c>pg_locks</c> (bounded) until it observes the bootstrap connection
/// blocked on a lock — failing the test outright if that is never observed, rather than
/// continuing regardless. Only once the barrier confirms the block does the competitor's
/// SECOND statement run, inside the same still-open transaction, followed by its commit.
/// It is this barrier — not merely the order the two statements are issued in — that
/// forces the exact interleaving a real deadlock needs, every run; asserted against the
/// real, fixed <see cref="DbBootstrapper"/>.
/// </para>
/// <para>
/// Lives in the "PostgresUpgrade" collection (shared with <see cref="UpgradePathTests"/>):
/// both need to freely drop/re-add <c>skipped_event_lines</c>, so both need the isolated
/// database that collection already provides; same-collection test classes run
/// sequentially, so the two classes cannot corrupt each other's state.
/// </para>
/// </remarks>
[Collection("PostgresUpgrade")]
public sealed class UpgradeRaceTests(PostgresFixture fixture)
{
    /// <summary>
    /// competitor "ingest" mirrors <c>NpgsqlTelemetryRepository.IngestAsync</c>
    /// (ingest_batch, then telemetry_event — Persistence/NpgsqlTelemetryRepository.cs:42-73).
    /// competitor "drainer" mirrors <c>ForgetQueueDrainer.DrainOneAsync</c> (telemetry_event,
    /// then ingest_batch — Background/ForgetQueueDrainer.cs:69-83) — the REVERSE order.
    /// </summary>
    [Theory]
    [InlineData("ingest")]
    [InlineData("drainer")]
    public async Task Bootstrap_DoesNotDeadlock_AgainstConcurrentCompetitor(string competitor)
    {
        // Run one untimed WARM bootstrap first. bootstrap.sql's final step,
        // ensure_partitions(current_date - 90, current_date + 7), pre-creates any partition
        // due for "today"; without this warm-up, a UTC date rollover between the fixture's
        // initial bootstrap and this case would make the timed bootstrap below create a
        // brand-new day partition while holding ingest_batch, which deadlocks against the
        // drainer competitor for a reason unrelated to the schema-evolution step under test.
        var warmBootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
        await warmBootstrapper.BootstrapAsync(CancellationToken.None);

        // Simulate a pre-#30 database so the schema-evolution step actually runs the ALTER
        // (and therefore actually takes ACCESS EXCLUSIVE on telemetry_event) — a warm start
        // with the column already present never attempts it at all, which would make this
        // test pass vacuously. Also matches UpgradePathTests' own precondition.
        await using (var conn = await fixture.DataSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "ALTER TABLE telemetry_event DROP COLUMN IF EXISTS skipped_event_lines", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var (firstSql, secondSql) = BuildCompetitorStatements(competitor);

        await using var competitorConn = await fixture.DataSource.OpenConnectionAsync();
        await using var competitorTx = await competitorConn.BeginTransactionAsync();

        // Competitor's FIRST statement: takes its first lock; transaction stays open.
        await using (var first = new NpgsqlCommand(firstSql, competitorConn, competitorTx))
        {
            await first.ExecuteNonQueryAsync();
        }

        // Start the bootstrap concurrently, bounded so a real regression fails this test
        // instead of hanging the suite (a genuine deadlock is caught by Postgres's own
        // detector, typically within ~1s; this is a backstop for anything else unbounded).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
        var bootstrapTask = Task.Run(() => bootstrapper.BootstrapAsync(cts.Token));

        // Barrier: wait until the bootstrap connection is observed WAITING on a lock, so
        // the competitor's second statement below is issued only once the bootstrap has
        // already taken whatever lock it is going to take. This barrier is what forces the
        // adversarial interleaving — the statement ordering above is necessary but not
        // sufficient on its own — so failing to observe the wait within the bound fails
        // the test outright instead of continuing as if nothing were wrong.
        await WaitUntilBlockedOrFailAsync(competitorConn.ProcessID);

        // Competitor's SECOND statement, in the SAME still-open transaction: takes the
        // conflicting lock in the opposite table order, then commits.
        await using (var second = new NpgsqlCommand(secondSql, competitorConn, competitorTx))
        {
            await second.ExecuteNonQueryAsync();
        }

        await competitorTx.CommitAsync();

        // Neither side may have failed — a 40P01 (or anything else) surfaces here.
        await bootstrapTask;

        Assert.Equal(1L, await CountNonDroppedColumnAsync());
    }

    private static (string First, string Second) BuildCompetitorStatements(string competitor)
    {
        if (competitor == "ingest")
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var first = $"INSERT INTO ingest_batch (idempotency_key, install_id, line_count) " +
                $"VALUES ('{key}', gen_random_uuid(), 1)";
            // The pre-#30, 20-column shape (matches this test's DROP COLUMN precondition;
            // also correct if the concurrent bootstrap has already re-added the column by
            // the time this runs, since it is not referenced either way).
            const string Second =
                "INSERT INTO telemetry_event (" +
                "install_id, event_timestamp, schema_version, tool_version, engine_version, dotnet_version, " +
                "run_count, scenario_count, step_pass, step_fail, step_env_error, step_inconclusive, " +
                "scenario_pass, scenario_fail, scenario_env_error, scenario_inconclusive, " +
                "step_families, step_providers, startup_ms, time_to_first_test_ms" +
                ") VALUES (" +
                "gen_random_uuid(), now(), 1, '1.0.0', '1.0.0', '.NET 8.0', " +
                "1, 1, 1, 0, 0, 0, 1, 0, 0, 0, '{}'::jsonb, '{}'::jsonb, 1, 1)";
            return (first, Second);
        }

        if (competitor == "drainer")
        {
            // Matches nothing (0 rows affected either way) — the point is the table-level
            // RowExclusiveLock each DELETE takes, not the row count.
            const string First = "DELETE FROM telemetry_event WHERE install_id = gen_random_uuid()";
            const string Second = "DELETE FROM ingest_batch WHERE install_id = gen_random_uuid()";
            return (First, Second);
        }

        throw new ArgumentOutOfRangeException(nameof(competitor), competitor, "Expected 'ingest' or 'drainer'.");
    }

    // Polls pg_locks (bounded: ~5s) for any NOT-granted lock that does not belong to the
    // competitor's own connection — in this isolated database, with only the competitor and
    // the bootstrap active, that can only be the bootstrap waiting on a lock. This barrier is
    // what forces the adversarial interleaving the test exists to exercise, so a run that
    // never observes the bootstrap blocked proves nothing and fails the test outright,
    // rather than letting the competitor's second statement continue regardless.
    private async Task WaitUntilBlockedOrFailAsync(int excludePid)
    {
        for (var i = 0; i < 200; i++)
        {
            await using var probeConn = await fixture.DataSource.OpenConnectionAsync();
            await using var probeCmd = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE NOT granted AND relation IS NOT NULL AND pid <> $1)",
                probeConn);
            probeCmd.Parameters.Add(new NpgsqlParameter { Value = excludePid, DataTypeName = "int4" });
            var waiting = (bool)(await probeCmd.ExecuteScalarAsync())!;
            if (waiting)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the bootstrap was never observed blocked on the competitor's lock");
    }

    private async Task<long> CountNonDroppedColumnAsync()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_attribute WHERE attrelid = 'telemetry_event'::regclass " +
            "AND attname = 'skipped_event_lines' AND NOT attisdropped",
            conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
