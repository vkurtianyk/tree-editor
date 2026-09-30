using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Web.Cache;
using static TreeEditor.Web.Tests.Cache.LocalCacheEditTests;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheAddChildTests
{
    [Fact]
    public async Task Added_child_is_a_new_pending_element_with_a_v7_id_and_no_request()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"]);
        var callsBefore = api.Calls.Count;
        var changes = 0;
        cache.Changed += () => changes++;

        var result = cache.AddChild(n["Alpha"].Id, "  Kid  ");

        Assert.Null(result.Error);
        Assert.Equal("Kid", result.Value);
        var id = Assert.NotNull(result.Id);
        Assert.Equal(7, id.Version);
        var element = cache.Find(id);
        Assert.NotNull(element);
        Assert.Equal<Guid>([n["Root"].Id, n["Alpha"].Id, id], element.Ancestors);
        Assert.Equal(
            new CachedElement(id, n["Alpha"].Id, element.Ancestors, "Kid", Version: 0, IsDeleted: false)
            {
                State = ElementState.New,
            },
            element);
        Assert.True(cache.HasPendingChanges);
        Assert.True(cache.CanApply);
        Assert.Equal(["Root", "  Alpha", "    Kid [new]"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(1, changes);
        Assert.Equal(callsBefore, api.Calls.Count);
    }

    [Fact]
    public async Task Child_can_be_added_under_an_unsaved_element()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"]);
        var parent = cache.AddChild(n["Root"].Id, "Parent").Id!.Value;

        var child = cache.AddChild(parent, "Child").Id!.Value;
        var grandchild = cache.AddChild(child, "Grandchild").Id!.Value;

        Assert.Equal([n["Root"].Id, parent, child, grandchild], cache.Find(grandchild)?.Ancestors);
        Assert.Equal(child, cache.Find(grandchild)?.ParentId);
        Assert.Equal(
            ["Root", "  Parent [new]", "    Child [new]", "      Grandchild [new]"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Value_colliding_with_a_cached_live_sibling_gets_a_local_suffix()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"], n["Alpha"], n["Beta"], n["Gone"]);
        var root = n["Root"].Id;

        Assert.Equal("beta (1)", cache.AddChild(root, "beta").Value);
        // Deleted siblings don't collide, and uncached ones are left to the server.
        Assert.Equal("Gone", cache.AddChild(root, "Gone").Value);
        Assert.Equal("Delta", cache.AddChild(root, "Delta").Value);
        // New siblings collide like loaded ones.
        Assert.Equal("delta (1)", cache.AddChild(root, "delta").Value);
        // Elements under another parent aren't siblings.
        Assert.Equal("Beta", cache.AddChild(n["Alpha"].Id, "Beta").Value);
    }

    [Theory]
    [InlineData("   ", "A value is required.")]
    [InlineData(null, "A value is required.")]
    public async Task Invalid_value_is_reported_at_once_and_adds_nothing(string? value, string error)
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"]);
        var changes = 0;
        cache.Changed += () => changes++;

        var result = cache.AddChild(n["Root"].Id, value);

        Assert.Equal(new AddChildResult(Id: null, Value: null, error), result);
        Assert.Equal("A value can have at most 255 characters.", cache.AddChild(n["Root"].Id, new string('x', 256)).Error);
        Assert.False(cache.HasPendingChanges);
        Assert.Equal(["Root"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Child_needs_a_cached_live_parent_and_no_apply_in_flight()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Gone"]);

        Assert.Throws<InvalidOperationException>(() => cache.AddChild(n["Alpha"].Id, "Kid"));
        Assert.Throws<InvalidOperationException>(() => cache.AddChild(n["Gone"].Id, "Kid"));

        cache.AddChild(n["Root"].Id, "Kid");
        var response = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => response.Task);
        var apply = cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => cache.AddChild(n["Root"].Id, "Other"));
        response.SetResult(new ApplyResponse([]));
        await apply;
    }

    [Fact]
    public async Task Edits_of_a_new_element_fold_into_its_single_insert()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"]);
        var id = cache.AddChild(n["Root"].Id, "Kid").Id!.Value;

        Assert.Equal("Alpha (1)", cache.EditValue(id, "Alpha").Value);
        Assert.Equal("Child", cache.EditValue(id, "  Child ").Value);

        Assert.Equal(ElementState.New, cache.Find(id)?.State);
        Assert.Equal(["Root", "  Alpha", "  Child [new]"], Outline.Of(cache.ViewTree, tree));
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        var request = Assert.Single(api.ApplyRequests);
        Assert.Equal([new NodeInsert(id, n["Root"].Id, "Child")], request.Inserts);
        Assert.Empty(request.Edits);
    }

    [Fact]
    public async Task Apply_sends_inserts_in_creation_order_with_the_pending_edits()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Epsilon");
        var parent = cache.AddChild(n["Alpha"].Id, "Parent").Id!.Value;
        var child = cache.AddChild(parent, "Child").Id!.Value;
        var top = cache.AddChild(n["Root"].Id, "Top").Id!.Value;
        cache.EditValue(parent, "Renamed");
        api.OnApplySucceed(newVersion: 5000);

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(api.ApplyRequests);
        Assert.Equal(
            [
                new NodeInsert(parent, n["Alpha"].Id, "Renamed"),
                new NodeInsert(child, parent, "Child"),
                new NodeInsert(top, n["Root"].Id, "Top"),
            ],
            request.Inserts);
        Assert.Equal([new NodeEdit(n["Beta"].Id, "Epsilon", n["Beta"].Version)], request.Edits);
        Assert.Empty(request.Deletes);
    }

    [Fact]
    public async Task After_apply_new_elements_keep_their_id_and_hold_the_server_value_and_version()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"]);
        var id = cache.AddChild(n["Root"].Id, "Delta").Id!.Value;
        // The server resolved a suffix against Delta, which isn't cached.
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(id, "Delta (1)", 5000, IsDeleted: false)])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(cache.HasPendingChanges);
        var element = cache.Find(id);
        Assert.NotNull(element);
        Assert.Equal<Guid>([n["Root"].Id, id], element.Ancestors);
        Assert.Equal(
            new CachedElement(id, n["Root"].Id, element.Ancestors, "Delta (1)", Version: 5000, IsDeleted: false),
            element);
        Assert.Equal(["Root", "  Alpha", "  Delta (1)"], Outline.Of(cache.ViewTree, tree));

        // From now on it is an ordinary element: a change is an edit with the applied version.
        cache.EditValue(id, "Omega");
        api.OnApplySucceed(newVersion: 5001);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Empty(api.ApplyRequests[^1].Inserts);
        Assert.Equal([new NodeEdit(id, "Omega", 5000)], api.ApplyRequests[^1].Edits);
    }

    [Fact]
    public async Task Discard_removes_new_elements_and_restores_edits()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"], n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        var parent = cache.AddChild(n["Alpha"].Id, "Parent").Id!.Value;
        var child = cache.AddChild(parent, "Child").Id!.Value;

        cache.DiscardAll();

        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.IsCached(parent));
        Assert.False(cache.IsCached(child));
        Assert.Equal(["Root", "  Alpha"], Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Discard_of_only_new_elements_notifies_a_change()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"]);
        cache.AddChild(n["Root"].Id, "Kid");
        var changes = 0;
        cache.Changed += () => changes++;

        cache.DiscardAll();

        Assert.Equal(1, changes);
        Assert.Equal(["Root"], Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Failure_keeps_new_elements_pending()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"]);
        var id = cache.AddChild(n["Root"].Id, "Kid").Id!.Value;
        var error = new HttpRequestException("The server failed.", null, HttpStatusCode.InternalServerError);
        api.OnApply(_ => Task.FromException<ApplyResponse>(error));

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.ApplyAsync(TestContext.Current.CancellationToken));

        Assert.True(cache.CanApply);
        Assert.Equal(ElementState.New, cache.Find(id)?.State);
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(api.ApplyRequests[0].Inserts, api.ApplyRequests[1].Inserts);
    }

    [Fact]
    public async Task Clear_drops_new_elements()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"]);
        var id = cache.AddChild(n["Root"].Id, "Kid").Id!.Value;

        cache.Clear();

        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.IsCached(id));
        await cache.LoadElementAsync(n["Root"].Id, TestContext.Current.CancellationToken);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Empty(api.ApplyRequests);
    }
}
