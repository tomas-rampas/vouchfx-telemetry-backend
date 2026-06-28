using Npgsql;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>
/// Runs <c>bootstrap.sql</c> (embedded resource) against the database on startup.
/// Uses a session-level advisory lock (key 152152153) so concurrent replicas are safe.
/// The script is fully idempotent (CREATE IF NOT EXISTS / CREATE OR REPLACE).
/// </summary>
internal sealed partial class DbBootstrapper(NpgsqlDataSource dataSource, ILogger<DbBootstrapper> logger)
{
    private const long BootstrapLockKey = 152152153L;

    [LoggerMessage(Level = LogLevel.Information, Message = "Running database bootstrap.")]
    private static partial void LogBootstrapRunning(ILogger<DbBootstrapper> logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database bootstrap completed.")]
    private static partial void LogBootstrapCompleted(ILogger<DbBootstrapper> logger);

    /// <summary>Runs the embedded <c>bootstrap.sql</c> under a session-level advisory lock.</summary>
    public async Task BootstrapAsync(CancellationToken ct)
    {
        LogBootstrapRunning(logger);
        var sql = ReadBootstrapSql();

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Set a 30-second lock_timeout so a stuck holder fails loudly rather than
        // blocking startup indefinitely. Reset after acquiring so the bootstrap DDL
        // itself is not constrained.
        await using (var ltCmd = new NpgsqlCommand("SET lock_timeout = '30s'", conn))
            await ltCmd.ExecuteNonQueryAsync(ct);

        await using (var lockCmd = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({BootstrapLockKey})", conn))
            await lockCmd.ExecuteNonQueryAsync(ct);

        await using (var resetCmd = new NpgsqlCommand("SET lock_timeout = DEFAULT", conn))
            await resetCmd.ExecuteNonQueryAsync(ct);

        try
        {
            await using var bootstrapCmd = new NpgsqlCommand(sql, conn);
            await bootstrapCmd.ExecuteNonQueryAsync(ct);
            LogBootstrapCompleted(logger);
        }
        finally
        {
            await using var unlockCmd = new NpgsqlCommand(
                $"SELECT pg_advisory_unlock({BootstrapLockKey})", conn);
            await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string ReadBootstrapSql()
    {
        var assembly = typeof(DbBootstrapper).Assembly;
        using var stream = assembly.GetManifestResourceStream("bootstrap.sql")
            ?? throw new InvalidOperationException(
                "Embedded resource 'bootstrap.sql' not found. " +
                "Ensure the EmbeddedResource item is present in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
