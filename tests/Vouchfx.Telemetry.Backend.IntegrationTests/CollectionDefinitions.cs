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
