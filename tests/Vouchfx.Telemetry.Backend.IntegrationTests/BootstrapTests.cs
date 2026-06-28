// CA1707: underscore test names are the xUnit convention.
#pragma warning disable CA1707

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Verifies that <see cref="DbBootstrapper"/> creates the expected schema objects
/// and is safe to run more than once (idempotency).
/// </summary>
[Collection("Postgres")]
public sealed class BootstrapTests(PostgresFixture fixture)
{
    /// <summary>Running bootstrap a second time must not throw.</summary>
    [Fact]
    public async Task Bootstrap_IsIdempotent_RunningTwiceDoesNotThrow()
    {
        // bootstrap.sql was already applied in the fixture; run it again.
        var bootstrapper = new DbBootstrapper(fixture.DataSource, NullLogger<DbBootstrapper>.Instance);
        await bootstrapper.BootstrapAsync(CancellationToken.None); // must not throw
    }

    /// <summary>All three core tables must exist after bootstrap.</summary>
    [Fact]
    public async Task Bootstrap_ExpectedTablesExist()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relname IN " +
            "('telemetry_event','ingest_batch','forget_queue')",
            conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(3L, count);
    }

    /// <summary>All four SQL helper functions must exist after bootstrap.</summary>
    [Fact]
    public async Task Bootstrap_ExpectedFunctionsExist()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'public' AND p.proname IN " +
            "('ensure_partition','ensure_partitions','drop_old_partitions','sweep_default')",
            conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(4L, count);
    }
}
