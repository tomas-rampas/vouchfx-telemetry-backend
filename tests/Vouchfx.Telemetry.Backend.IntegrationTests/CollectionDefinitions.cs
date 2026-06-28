// CA1711: the "Collection" suffix is intentional — xUnit collection definition naming convention.
#pragma warning disable CA1711

using Xunit;

namespace Vouchfx.Telemetry.Backend.IntegrationTests;

/// <summary>
/// xUnit collection definition that shares a single <see cref="PostgresFixture"/> instance
/// across all tests in the "Postgres" collection.
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// Isolated xUnit collection for the production-config boot test. Uses its own
/// <see cref="PostgresFixture"/> instance (separate Testcontainers database) so that
/// the test's process-level environment-variable mutations do not affect the Postgres
/// collection, and so that the <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// started by the boot test cannot interfere with repository-level integration tests.
/// </summary>
[CollectionDefinition("ProductionBoot")]
public sealed class ProductionBootCollection : ICollectionFixture<PostgresFixture>
{
}
