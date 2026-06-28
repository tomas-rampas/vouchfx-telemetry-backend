using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>
/// Placeholder <see cref="ITelemetryRepository"/> used when no real database is configured.
/// Every call throws <see cref="TransientStorageException"/> so the ingest endpoint returns
/// HTTP 503, which signals the caller to retry once the backend is provisioned.
/// </summary>
/// <remarks>
/// Replace this registration with the Npgsql implementation once the database is available.
/// </remarks>
internal sealed class UnconfiguredTelemetryRepository : ITelemetryRepository
{
    /// <inheritdoc/>
    public Task<int> IngestAsync(
        IReadOnlyList<TelemetryEvent> events,
        string idempotencyKey,
        CancellationToken ct)
        => throw new TransientStorageException(
            "No persistence backend is configured. Provision the database and register the Npgsql repository.");

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <see langword="false"/> — no real persistence backend is configured.
    /// The <c>/readyz</c> probe maps this to HTTP 503, preventing Azure Container Apps
    /// from routing traffic until the Npgsql repository is registered.
    /// </remarks>
    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(false);
}
