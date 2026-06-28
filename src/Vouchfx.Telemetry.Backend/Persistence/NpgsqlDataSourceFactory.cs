using Npgsql;

namespace Vouchfx.Telemetry.Backend.Persistence;

/// <summary>Builds a pooled <see cref="NpgsqlDataSource"/> from a connection string.</summary>
internal static class NpgsqlDataSourceFactory
{
    /// <summary>Creates and returns a pooled <see cref="NpgsqlDataSource"/>.</summary>
    public static NpgsqlDataSource Build(string connectionString)
    {
        var dsBuilder = new NpgsqlDataSourceBuilder(connectionString);
        // Pin the session timezone to UTC so TIMESTAMPTZ reads and writes are always
        // in UTC regardless of the host server's timezone setting.
        dsBuilder.ConnectionStringBuilder.Timezone = "UTC";
        return dsBuilder.Build();
    }
}
