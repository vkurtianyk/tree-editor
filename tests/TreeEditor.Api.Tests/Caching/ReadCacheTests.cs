using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Caching;

/// <summary>
/// The backend read cache: repeated lists and loads skip the database, and a committed Apply or a Reset
/// makes the next read return what the database now holds.
/// </summary>
public sealed class ReadCacheTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Repeated_list_and_load_are_served_from_the_cache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var home = tree.Root("Home");
        var kitchen = tree.Child(home, "Kitchen");
        await api.ArrangeAsync(tree, cancellationToken);
        await ListAsync(api, parentId: null, after: null, cancellationToken);
        await ListAsync(api, home.Id, after: null, cancellationToken);
        await LoadAsync(api, kitchen.Id, cancellationToken);

        // Changed behind the API's back: no Apply committed, so nothing invalidates the cached reads.
        await ChangeValueInDatabaseAsync(api, home.Id, "House", cancellationToken);
        await ChangeValueInDatabaseAsync(api, kitchen.Id, "Pantry", cancellationToken);

        Assert.Equal(["Home"], Values(await ListAsync(api, parentId: null, after: null, cancellationToken)));
        Assert.Equal(["Kitchen"], Values(await ListAsync(api, home.Id, after: null, cancellationToken)));
        Assert.Equal("Kitchen", (await LoadAsync(api, kitchen.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Unknown_element_and_its_children_are_read_again_once_it_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var later = new TreeBuilder();
        var shed = later.Root("Shed");
        later.Child(shed, "Rake");
        using (var unknown = await api.Client.GetAsync(ApiRoutes.NodeById(shed.Id), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        Assert.Empty((await ListAsync(api, shed.Id, after: null, cancellationToken)).Items);

        await api.ArrangeAsync(later, cancellationToken);

        Assert.Equal("Shed", (await LoadAsync(api, shed.Id, cancellationToken)).Value);
        Assert.Equal(["Rake"], Values(await ListAsync(api, shed.Id, after: null, cancellationToken)));
    }

    private static string[] Values(ChildrenPage page) => [.. page.Items.Select(item => item.Value)];

    private static async Task<ChildrenPage> ListAsync(
        ApiHarness api, Guid? parentId, ChildrenCursor? after, CancellationToken cancellationToken)
    {
        var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId, after), cancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<NodeDetails> LoadAsync(ApiHarness api, Guid id, CancellationToken cancellationToken)
    {
        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(id), cancellationToken);
        Assert.NotNull(node);
        return node;
    }

    private static async Task ChangeValueInDatabaseAsync(
        ApiHarness api, Guid id, string value, CancellationToken cancellationToken)
    {
        await using var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        await db.Nodes.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Value, value), cancellationToken);
    }
}
