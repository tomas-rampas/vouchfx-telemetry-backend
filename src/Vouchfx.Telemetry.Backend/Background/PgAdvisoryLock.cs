using Npgsql;

namespace Vouchfx.Telemetry.Backend.Background;

/// <summary>
/// Wraps a PostgreSQL session-level advisory lock (key 152152152).
/// Dispose releases the lock and closes the connection.
/// </summary>
internal sealed class PgAdvisoryLock(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    private const long MaintenanceLockKey = 152152152L;
    private NpgsqlConnection? _connection;
    private bool _acquired;

    /// <summary>
    /// Attempts to acquire the advisory lock.
    /// Returns <see langword="true"/> if acquired; <see langword="false"/> if another session holds it.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        _connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT pg_try_advisory_lock({MaintenanceLockKey})", _connection);
        _acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        if (!_acquired)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        return _acquired;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_acquired && _connection is not null)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"SELECT pg_advisory_unlock({MaintenanceLockKey})", _connection);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // Connection may be broken; the lock will be released when the session closes.
            }
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _acquired = false;
    }
}
