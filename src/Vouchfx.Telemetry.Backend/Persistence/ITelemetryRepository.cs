using Vouchfx.Telemetry.Backend.Contracts;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>
/// Persistence contract for telemetry event batches.
/// </summary>
public interface ITelemetryRepository
{
    /// <summary>
    /// Ingests a batch of telemetry events identified by <paramref name="idempotencyKey"/>.
    /// </summary>
    /// <param name="events">The parsed events to persist.</param>
    /// <param name="idempotencyKey">
    /// SHA-256 hex key computed by the engine client; duplicate batches (same key) must
    /// be de-duplicated and return <c>0</c> without double-counting.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of <em>new</em> rows persisted, or <c>0</c> when the
    /// <paramref name="idempotencyKey"/> was already seen (idempotent re-send).
    /// </returns>
    /// <exception cref="TransientStorageException">
    /// Thrown on transient database faults. The ingest endpoint maps this to HTTP 503.
    /// </exception>
    Task<int> IngestAsync(IReadOnlyList<TelemetryEvent> events, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Performs a lightweight health check to determine whether the persistence
    /// backend is available and ready to accept events.
    /// </summary>
    /// <remarks>
    /// The real Npgsql repository will implement this as a lightweight DB ping
    /// (e.g. <c>SELECT 1</c>). Returns <see langword="false"/> when no real backend
    /// is configured; the <c>/readyz</c> probe maps this to HTTP 503, preventing
    /// Azure Container Apps from routing traffic to a silent no-op placeholder.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the backend is ready; otherwise <see langword="false"/>.</returns>
    Task<bool> IsReadyAsync(CancellationToken ct);
}
