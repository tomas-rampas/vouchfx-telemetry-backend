// CA1707: underscore test names are the xUnit convention.
#pragma warning disable CA1707

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouchfx.Telemetry.Backend.Background;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="NpgsqlTelemetryRepository"/>: ingest, deduplication,
/// forget round-trip, and health probe.
/// </summary>
[Collection("Postgres")]
public sealed class RepositoryTests(PostgresFixture fixture)
{
    private NpgsqlTelemetryRepository CreateRepo() =>
        new(fixture.DataSource, TimeProvider.System, NullLogger<NpgsqlTelemetryRepository>.Instance);

    // ── Happy path ────────────────────────────────────────────────────────────

    /// <summary>A fresh batch with a unique key must insert one row.</summary>
    [Fact]
    public async Task IngestAsync_HappyPath_InsertsRows()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();
        var events = new[] { TestData.MakeEvent(installId: installId) };
        var key = TestData.MakeIdempotencyKey();

        var newRows = await repo.IngestAsync(events, key, CancellationToken.None);

        Assert.Equal(1, newRows);
        Assert.Equal(1L, await CountEventRowsAsync(installId));
    }

    // ── Batch dedup ───────────────────────────────────────────────────────────

    /// <summary>Replaying the same idempotency key must return 0 and not double-insert.</summary>
    [Fact]
    public async Task IngestAsync_SameIdempotencyKey_ReturnZeroOnSecondCall()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();
        var events = new[] { TestData.MakeEvent(installId: installId) };
        var key = TestData.MakeIdempotencyKey();

        var first = await repo.IngestAsync(events, key, CancellationToken.None);
        var second = await repo.IngestAsync(events, key, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);                     // duplicate batch → 0
        Assert.Equal(1L, await CountEventRowsAsync(installId)); // row count unchanged
    }

    // ── Row-level dedup ───────────────────────────────────────────────────────

    /// <summary>Two different keys carrying identical events must produce exactly one row (ON CONFLICT DO NOTHING).</summary>
    [Fact]
    public async Task IngestAsync_DifferentKeysSameEvent_SecondConflictIsDiscarded()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;
        // Two events with identical (install_id, event_timestamp, schema_version)
        var ev = TestData.MakeEvent(installId: installId, timestamp: ts);

        var key1 = TestData.MakeIdempotencyKey();
        var key2 = TestData.MakeIdempotencyKey();

        var first = await repo.IngestAsync(new[] { ev }, key1, CancellationToken.None);
        var second = await repo.IngestAsync(new[] { ev }, key2, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);  // ON CONFLICT at row level; no new row
        Assert.Equal(1L, await CountEventRowsAsync(installId));
    }

    // ── IsReadyAsync ──────────────────────────────────────────────────────────

    /// <summary>Against a live database the repository must report ready.</summary>
    [Fact]
    public async Task IsReadyAsync_ReturnsTrue_AgainstLiveDb()
    {
        var repo = CreateRepo();
        var ready = await repo.IsReadyAsync(CancellationToken.None);
        Assert.True(ready);
    }

    // ── Forget round-trip ─────────────────────────────────────────────────────

    /// <summary>Draining a forget request must remove event rows, batch rows, and mark the queue entry processed.</summary>
    [Fact]
    public async Task ForgetRoundTrip_DeletesRowsAndSetsProcessedAt()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();
        var events = new[] { TestData.MakeEvent(installId: installId) };
        var key = TestData.MakeIdempotencyKey();

        // Ingest first
        await repo.IngestAsync(events, key, CancellationToken.None);
        Assert.Equal(1L, await CountEventRowsAsync(installId));

        // Enqueue forget
        await repo.EnqueueForgetAsync(installId, CancellationToken.None);

        // Drain
        var drainer = new ForgetQueueDrainer(
            fixture.DataSource,
            NullLogger<ForgetQueueDrainer>.Instance);
        await drainer.DrainAsync(CancellationToken.None);

        // All telemetry_event rows for install are gone
        Assert.Equal(0L, await CountEventRowsAsync(installId));

        // ingest_batch rows for install are gone
        Assert.Equal(0L, await CountBatchRowsAsync(installId));

        // forget_queue.processed_at is set
        Assert.True(await ForgetProcessedAsync(installId));
    }

    /// <summary>Re-enqueueing after a processed forget must reset processed_at to NULL.</summary>
    [Fact]
    public async Task EnqueueForgetAsync_Idempotent_ReopensProcessedRequest()
    {
        var repo = CreateRepo();
        var installId = Guid.NewGuid();

        // First enqueue + drain
        await repo.EnqueueForgetAsync(installId, CancellationToken.None);
        var drainer = new ForgetQueueDrainer(
            fixture.DataSource, NullLogger<ForgetQueueDrainer>.Instance);
        await drainer.DrainAsync(CancellationToken.None);
        Assert.True(await ForgetProcessedAsync(installId));

        // Second enqueue should re-open the request (processed_at → NULL)
        await repo.EnqueueForgetAsync(installId, CancellationToken.None);
        Assert.False(await ForgetProcessedAsync(installId));
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

    private async Task<long> CountBatchRowsAsync(Guid installId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM ingest_batch WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> ForgetProcessedAsync(Guid installId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT processed_at IS NOT NULL FROM forget_queue WHERE install_id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = installId, DataTypeName = "uuid" });
        var result = await cmd.ExecuteScalarAsync();
        return result is bool b && b;
    }
}
