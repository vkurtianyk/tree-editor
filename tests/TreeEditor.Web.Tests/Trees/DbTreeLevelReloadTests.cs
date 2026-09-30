using System.Net;
using System.Net.Http.Json;
using TreeEditor.Contracts;
using TreeEditor.Web.Api;
using TreeEditor.Web.Trees;

namespace TreeEditor.Web.Tests.Trees;

/// <summary>A reload after an Apply or a Reset shows what the database has now, opened as far as it was.</summary>
public sealed class DbTreeLevelReloadTests
{
    private static readonly NodeListItem Alpha = Item(1, "Alpha", hasChildren: true);
    private static readonly NodeListItem A1 = Item(2, "A1", hasChildren: true);
    private static readonly NodeListItem A2 = Item(3, "A2");
    private static readonly NodeListItem Beta = Item(4, "Beta", hasChildren: true);
    private static readonly NodeListItem B1 = Item(5, "B1");
    private static readonly NodeListItem Gamma = Item(6, "Gamma");

    [Fact]
    public async Task Reload_expands_again_the_rows_that_were_expanded_with_their_children_as_they_are_now()
    {
        var database = new Listings();
        database.Set(null, [Alpha, Beta]);
        database.Set(Alpha.Id, [A1]);
        database.Set(A1.Id, [A2]);
        database.Set(Beta.Id, [B1]);
        var api = database.Client();
        var roots = new DbTreeLevel(parentId: null);
        await roots.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        var alpha = await ExpandAsync(roots, Alpha, api);
        await ExpandAsync(alpha, A1, api);
        await ExpandAsync(roots, Beta, api);
        roots.Nodes.Single(node => node.Item == Beta).Collapse();
        var renamed = A2 with { Value = "A2 elsewhere" };
        database.Set(A1.Id, [renamed, Gamma]);

        var fresh = await roots.ReloadAsync(api, TestContext.Current.CancellationToken);

        var freshAlpha = fresh.Nodes.Single(node => node.Item == Alpha);
        Assert.True(freshAlpha.IsExpanded);
        var freshA1 = Assert.Single(freshAlpha.Children!.Nodes);
        Assert.True(freshA1.IsExpanded);
        Assert.Equal([renamed, Gamma], freshA1.Children!.Nodes.Select(node => node.Item));
        var freshBeta = fresh.Nodes.Single(node => node.Item == Beta);
        Assert.False(freshBeta.IsExpanded);
        Assert.Null(freshBeta.Children);
    }

    [Fact]
    public async Task Reload_leaves_collapsed_a_row_that_has_no_children_any_more()
    {
        var database = new Listings();
        database.Set(null, [Alpha]);
        database.Set(Alpha.Id, [A1]);
        var api = database.Client();
        var roots = new DbTreeLevel(parentId: null);
        await roots.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        await ExpandAsync(roots, Alpha, api);
        database.Set(null, [Alpha with { HasChildren = false }]);

        var fresh = await roots.ReloadAsync(api, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(fresh.Nodes).IsExpanded);
    }

    [Fact]
    public async Task Reload_loads_as_many_rows_as_were_loaded()
    {
        var database = new Listings();
        database.Set(null, [Alpha, Beta], [Gamma]);
        var api = database.Client();
        var roots = new DbTreeLevel(parentId: null);
        await roots.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        await roots.LoadNextPageAsync(api, TestContext.Current.CancellationToken);

        var fresh = await roots.ReloadAsync(api, TestContext.Current.CancellationToken);

        Assert.Equal([Alpha, Beta, Gamma], fresh.Nodes.Select(node => node.Item));
        Assert.False(fresh.HasMore);
    }

    [Fact]
    public async Task Find_returns_the_row_as_the_reload_listed_it()
    {
        var database = new Listings();
        database.Set(null, [Alpha, Beta]);
        database.Set(Alpha.Id, [A1]);
        var api = database.Client();
        var roots = new DbTreeLevel(parentId: null);
        await roots.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        await ExpandAsync(roots, Alpha, api);
        var deleted = A1 with { IsDeleted = true };
        database.Set(Alpha.Id, [deleted]);
        database.Set(null, [Alpha]);

        var fresh = await roots.ReloadAsync(api, TestContext.Current.CancellationToken);

        Assert.Equal(deleted, fresh.Find(A1.Id));
        Assert.Null(fresh.Find(Beta.Id));
    }

    private static async Task<DbTreeLevel> ExpandAsync(DbTreeLevel level, NodeListItem item, TreeApiClient api)
    {
        var children = level.Nodes.Single(node => node.Item == item).Expand();
        await children.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        return children;
    }

    private static NodeListItem Item(int id, string value, bool hasChildren = false) =>
        new(Guid.Parse($"0199a000-0000-7000-8000-{id:x12}"), value, IsDeleted: false, hasChildren);

    /// <summary>The database side: the children of each parent, in pages, answered by route.</summary>
    private sealed class Listings : HttpMessageHandler
    {
        // Per parent (its first page's route), the pages by route.
        private readonly Dictionary<string, Dictionary<string, ChildrenPage>> listings = [];

        /// <summary>Replaces the children of the parent with the pages given, each continuing from the one before.</summary>
        public void Set(Guid? parentId, params IReadOnlyList<NodeListItem>[] items)
        {
            var pages = listings[ApiRoutes.ChildrenOf(parentId)] = [];
            ChildrenCursor? after = null;
            for (var index = 0; index < items.Length; index++)
            {
                var last = items[index][^1];
                var next = index < items.Length - 1 ? new ChildrenCursor(last.Value.ToLowerInvariant(), last.Id) : null;
                pages[ApiRoutes.ChildrenOf(parentId, after)] = new ChildrenPage(items[index], next is not null, next);
                after = next;
            }
        }

        public TreeApiClient Client() => new(new HttpClient(this) { BaseAddress = new Uri("http://localhost/") });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.PathAndQuery;
            var page = listings.Values.SelectMany(pages => pages).Where(entry => entry.Key == route)
                .Select(entry => entry.Value).SingleOrDefault()
                ?? throw new InvalidOperationException($"No listing for {route}.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) });
        }
    }
}
