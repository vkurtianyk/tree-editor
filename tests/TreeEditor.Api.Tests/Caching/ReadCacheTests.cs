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

    [Fact]
    public async Task List_and_load_right_after_an_Apply_return_the_new_values_and_versions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var home = tree.Root("Home");
        var kitchen = tree.Child(home, "Kitchen");
        var knife = tree.Child(kitchen, "Knife");
        await api.ArrangeAsync(tree, cancellationToken);
        await ListAsync(api, home.Id, after: null, cancellationToken);
        await ListAsync(api, kitchen.Id, after: null, cancellationToken);
        await LoadAsync(api, kitchen.Id, cancellationToken);
        await LoadAsync(api, knife.Id, cancellationToken);

        var applied = await ApplyEditsAsync(
            api,
            [new NodeEdit(kitchen.Id, "Pantry", kitchen.Version), new NodeEdit(knife.Id, "Fork", knife.Version)],
            cancellationToken);

        Assert.Equal(["Pantry"], Values(await ListAsync(api, home.Id, after: null, cancellationToken)));
        Assert.Equal(["Fork"], Values(await ListAsync(api, kitchen.Id, after: null, cancellationToken)));
        foreach (var node in applied.Nodes)
        {
            var loaded = await LoadAsync(api, node.Id, cancellationToken);
            Assert.Equal(node.Value, loaded.Value);
            Assert.Equal(node.Version, loaded.Version);
        }
    }

    [Fact]
    public async Task Children_lists_right_after_an_Apply_inserting_show_the_new_children_with_their_final_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var home = tree.Root("Home");
        var garden = tree.Child(home, "Garden");
        var kitchen = tree.Child(home, "Kitchen");
        tree.Child(kitchen, "Knife");
        await api.ArrangeAsync(tree, cancellationToken);
        Assert.False((await ListAsync(api, home.Id, after: null, cancellationToken)).Items[0].HasChildren);
        Assert.Empty((await ListAsync(api, garden.Id, after: null, cancellationToken)).Items);
        Assert.Equal(["Knife"], Values(await ListAsync(api, kitchen.Id, after: null, cancellationToken)));

        var applied = await ApplyAsync(
            api,
            new ApplyRequest(
                [
                    new NodeInsert(Guid.CreateVersion7(), kitchen.Id, " knife "),
                    new NodeInsert(Guid.CreateVersion7(), garden.Id, "Rake"),
                ],
                [],
                []),
            cancellationToken);

        Assert.Equal(["knife (1)", "Rake"], applied.Nodes.Select(node => node.Value));
        Assert.Equal(["Knife", "knife (1)"], Values(await ListAsync(api, kitchen.Id, after: null, cancellationToken)));
        Assert.Equal(["Rake"], Values(await ListAsync(api, garden.Id, after: null, cancellationToken)));
        Assert.True((await ListAsync(api, home.Id, after: null, cancellationToken)).Items[0].HasChildren);
    }

    [Fact]
    public async Task Roots_list_and_load_right_after_an_Apply_renaming_a_root_return_the_new_value_and_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var home = tree.Root("Home");
        tree.Root("Garden");
        await api.ArrangeAsync(tree, cancellationToken);
        await ListAsync(api, parentId: null, after: null, cancellationToken);
        await LoadAsync(api, home.Id, cancellationToken);

        var applied = await ApplyEditsAsync(api, [new NodeEdit(home.Id, "Attic", home.Version)], cancellationToken);

        Assert.Equal(["Attic", "Garden"], Values(await ListAsync(api, parentId: null, after: null, cancellationToken)));
        var loaded = await LoadAsync(api, home.Id, cancellationToken);
        Assert.Equal("Attic", loaded.Value);
        Assert.Equal(Assert.Single(applied.Nodes).Version, loaded.Version);
    }

    [Fact]
    public async Task Root_renamed_onto_a_later_roots_page_is_listed_there_right_after_the_Apply()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var roots = Enumerable.Range(0, ChildrenPage.PageSize + 1).Select(i => tree.Root($"Root {i:D3}")).ToList();
        await api.ArrangeAsync(tree, cancellationToken);
        var first = await ListAsync(api, parentId: null, after: null, cancellationToken);
        Assert.Equal(
            [$"Root {ChildrenPage.PageSize:D3}"],
            Values(await ListAsync(api, parentId: null, first.Next, cancellationToken)));

        // Moves from the first page to the end of the second, which never listed it before.
        await ApplyEditsAsync(api, [new NodeEdit(roots[0].Id, "Zulu", roots[0].Version)], cancellationToken);

        Assert.Equal(
            [$"Root {ChildrenPage.PageSize:D3}", "Zulu"],
            Values(await ListAsync(api, parentId: null, first.Next, cancellationToken)));
    }

    [Fact]
    public async Task List_and_load_right_after_a_Reset_return_the_seed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var seed = new TreeBuilder();
        var home = seed.Root("Home");
        var kitchen = seed.Child(home, "Kitchen");
        await api.ArrangeSeedAsync(seed, cancellationToken);
        NodeEdit[] edits =
        [
            new(home.Id, "Attic", await VersionInDatabaseAsync(api, home.Id, cancellationToken)),
            new(kitchen.Id, "Pantry", await VersionInDatabaseAsync(api, kitchen.Id, cancellationToken)),
        ];
        await ApplyEditsAsync(api, edits, cancellationToken);
        Assert.Equal(["Attic"], Values(await ListAsync(api, parentId: null, after: null, cancellationToken)));
        Assert.Equal(["Pantry"], Values(await ListAsync(api, home.Id, after: null, cancellationToken)));
        Assert.Equal("Pantry", (await LoadAsync(api, kitchen.Id, cancellationToken)).Value);

        using var reset = await api.Client.PostAsync(ApiRoutes.Reset, content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(["Home"], Values(await ListAsync(api, parentId: null, after: null, cancellationToken)));
        Assert.Equal(["Kitchen"], Values(await ListAsync(api, home.Id, after: null, cancellationToken)));
        var loaded = await LoadAsync(api, kitchen.Id, cancellationToken);
        Assert.Equal("Kitchen", loaded.Value);
        Assert.Equal(await VersionInDatabaseAsync(api, kitchen.Id, cancellationToken), loaded.Version);
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

    private static Task<ApplyResponse> ApplyEditsAsync(
        ApiHarness api, NodeEdit[] edits, CancellationToken cancellationToken) =>
        ApplyAsync(api, new ApplyRequest([], edits, []), cancellationToken);

    private static async Task<ApplyResponse> ApplyAsync(
        ApiHarness api, ApplyRequest request, CancellationToken cancellationToken)
    {
        using var response = await api.Client.PostAsJsonAsync(ApiRoutes.Apply, request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>(cancellationToken);
        Assert.NotNull(applied);
        return applied;
    }

    private static async Task ChangeValueInDatabaseAsync(
        ApiHarness api, Guid id, string value, CancellationToken cancellationToken)
    {
        await using var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        await db.Nodes.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Value, value), cancellationToken);
    }

    private static async Task<uint> VersionInDatabaseAsync(ApiHarness api, Guid id, CancellationToken cancellationToken)
    {
        await using var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        return await db.Nodes.Where(n => n.Id == id).Select(n => n.Version).SingleAsync(cancellationToken);
    }
}
