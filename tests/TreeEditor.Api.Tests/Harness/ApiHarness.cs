using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Harness;

/// <summary>
/// The API hosted in-process on a database of its own. Tests arrange a tree, then talk to the API over <see cref="Client"/>.
/// </summary>
public sealed class ApiHarness : IAsyncDisposable
{
    private readonly PostgresFixture postgres;
    private readonly TestDatabase database;
    private readonly WebApplicationFactory<Program> factory;

    private ApiHarness(PostgresFixture postgres, TestDatabase database)
    {
        this.postgres = postgres;
        this.database = database;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting($"ConnectionStrings:{TreeDbContext.ConnectionName}", database.ConnectionString));
        Client = factory.CreateClient();
    }

    public HttpClient Client { get; }

    public static async Task<ApiHarness> StartAsync(PostgresFixture postgres, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        var database = await postgres.CreateDatabaseAsync(cancellationToken);
        return new ApiHarness(postgres, database);
    }

    /// <summary>Writes the tree straight to the database.</summary>
    public async Task ArrangeAsync(TreeBuilder tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        db.Nodes.AddRange(tree.Nodes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync();
        await postgres.DropDatabaseAsync(database, CancellationToken.None);
    }
}
