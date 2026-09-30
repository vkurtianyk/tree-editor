using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Data;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace TreeEditor.Api.Tests.Harness;

/// <summary>
/// One PostgreSQL container for the whole test run. It holds a migrated, empty template database;
/// every test gets its own copy (<see cref="CreateDatabaseAsync"/>), so tests arrange their own tree and run in parallel.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Same image as the AppHost.
    private const string Image = "postgres:18.3";
    private const string TemplateDatabase = "tree_template";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(Image).Build();
    private readonly SemaphoreSlim createLock = new(1, 1);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken);
        await ExecuteAsync($"CREATE DATABASE {TemplateDatabase}", cancellationToken);

        // Unpooled, so no idle connection keeps the template busy: CREATE DATABASE ... TEMPLATE needs it unused.
        var options = new DbContextOptionsBuilder<TreeDbContext>()
            .UseNpgsql(ConnectionStringFor(TemplateDatabase, pooling: false))
            .Options;
        await using var db = new TreeDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        createLock.Dispose();
        await container.DisposeAsync();
    }

    /// <summary>Creates a migrated, empty database for one test.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"test_{Guid.NewGuid():N}";

        // Copies from one template run one at a time.
        await createLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync($"CREATE DATABASE {name} TEMPLATE {TemplateDatabase}", cancellationToken);
        }
        finally
        {
            createLock.Release();
        }

        return new TestDatabase(name, ConnectionStringFor(name, pooling: true));
    }

    public async Task DropDatabaseAsync(TestDatabase database, CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        await ExecuteAsync($"DROP DATABASE IF EXISTS {database.Name} WITH (FORCE)", cancellationToken);
    }

    private string ConnectionStringFor(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        }.ConnectionString;

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>A database owned by one test.</summary>
public sealed record TestDatabase(string Name, string ConnectionString);
