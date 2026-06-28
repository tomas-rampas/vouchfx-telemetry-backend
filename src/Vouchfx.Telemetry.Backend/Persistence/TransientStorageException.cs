namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>
/// Thrown by <see cref="ITelemetryRepository"/> implementations when a transient storage
/// fault occurs (e.g., the database is temporarily unavailable). The ingest endpoint maps
/// this exception to HTTP 503 Service Unavailable with a <c>Retry-After</c> header.
/// </summary>
public sealed class TransientStorageException : Exception
{
    /// <inheritdoc cref="Exception(string)"/>
    public TransientStorageException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="Exception(string, Exception)"/>
    public TransientStorageException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
