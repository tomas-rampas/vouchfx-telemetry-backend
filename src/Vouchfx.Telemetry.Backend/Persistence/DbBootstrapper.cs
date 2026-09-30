using Npgsql;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>
/// Runs <c>bootstrap.sql</c> and <c>bootstrap-schema-evolution.sql</c> (embedded
/// resources) against the database on startup. Uses a session-level advisory lock
/// (key 152152153) so concurrent replicas are safe. Both scripts are fully idempotent
/// (CREATE IF NOT EXISTS / CREATE OR REPLACE / a catalog-guarded schema-evolution step).
/// </summary>
/// <remarks>
/// The two scripts run as TWO SEPARATE <see cref="NpgsqlCommand"/>s — deliberately, not
/// merged into one (issue #30). A single multi-statement command runs as
/// one implicit Postgres transaction; a schema-evolution step that takes ACCESS EXCLUSIVE
/// on one table, sharing that transaction with the main script's own lock on a second
/// table, can deadlock (40P01) against any other transaction that locks the same two
/// tables in the opposite order — regardless of which script the ACCESS EXCLUSIVE step
/// lives in or where in that one script it runs. Running it as its own command gives it
/// its own transaction, touching only the one table it needs — which cannot form a
/// lock-order cycle with this service's own transactions: ingest and the forget drainer
/// each lock this table and one other in a fixed order; the maintenance job's
/// <c>ensure_partition</c> and <c>drop_old_partitions</c> lock the table before any
/// partition, and <c>sweep_default</c> locks only the default partition directly, never
/// the table itself. See bootstrap-schema-evolution.sql's header for the full analysis,
/// the rules that keep a future step added to that file safe, and
/// <c>UpgradeRaceTests</c> (IntegrationTests) for the regression test.
/// </remarks>
internal sealed partial class DbBootstrapper(NpgsqlDataSource dataSource, ILogger<DbBootstrapper> logger)
{
    private const long BootstrapLockKey = 152152153L;

    [LoggerMessage(Level = LogLevel.Information, Message = "Running database bootstrap.")]
    private static partial void LogBootstrapRunning(ILogger<DbBootstrapper> logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database bootstrap completed.")]
    private static partial void LogBootstrapCompleted(ILogger<DbBootstrapper> logger);

    /// <summary>
    /// Runs the embedded bootstrap scripts under a session-level advisory lock.
    /// </summary>
    public async Task BootstrapAsync(CancellationToken ct)
    {
        LogBootstrapRunning(logger);
        var mainSql = ReadEmbeddedSql("bootstrap.sql");
        var schemaEvolutionSql = ReadEmbeddedSql("bootstrap-schema-evolution.sql");

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Set a 30-second lock_timeout so a stuck holder fails loudly rather than
        // blocking startup indefinitely. Reset after acquiring so the bootstrap DDL
        // itself is not constrained by it (the schema-evolution step below bounds its
        // OWN lock wait independently, inside its own transaction).
        await using (var ltCmd = new NpgsqlCommand("SET lock_timeout = '30s'", conn))
            await ltCmd.ExecuteNonQueryAsync(ct);

        await using (var lockCmd = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({BootstrapLockKey})", conn))
            await lockCmd.ExecuteNonQueryAsync(ct);

        await using (var resetCmd = new NpgsqlCommand("SET lock_timeout = DEFAULT", conn))
            await resetCmd.ExecuteNonQueryAsync(ct);

        try
        {
            await using (var bootstrapCmd = new NpgsqlCommand(mainSql, conn))
                await bootstrapCmd.ExecuteNonQueryAsync(ct);

            // Deliberately a SEPARATE command/transaction from mainSql above — see the
            // class remarks and bootstrap-schema-evolution.sql's header. Still on the
            // same connection, so still covered by the advisory lock held above.
            await using (var evolutionCmd = new NpgsqlCommand(schemaEvolutionSql, conn))
                await evolutionCmd.ExecuteNonQueryAsync(ct);

            LogBootstrapCompleted(logger);
        }
        finally
        {
            await using var unlockCmd = new NpgsqlCommand(
                $"SELECT pg_advisory_unlock({BootstrapLockKey})", conn);
            await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string ReadEmbeddedSql(string logicalName)
    {
        var assembly = typeof(DbBootstrapper).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found. " +
                "Ensure the EmbeddedResource item is present in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
