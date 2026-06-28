using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Vouchfx.Telemetry.Backend.Persistence;
using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// Starts a postgres:16 container once per collection, applies bootstrap.sql, and exposes
/// a connection string and a pre-built <see cref="NpgsqlDataSource"/> for tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Gets the pooled <see cref="NpgsqlDataSource"/> connected to the test database.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Gets the Npgsql connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16").Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DataSource = NpgsqlDataSourceFactory.Build(ConnectionString);

        // Apply bootstrap.sql (idempotent DDL)
        var bootstrapper = new DbBootstrapper(DataSource, NullLogger<DbBootstrapper>.Instance);
        await bootstrapper.BootstrapAsync(CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
