using TreeEditor.Contracts;
using TreeEditor.Web.Cache;
using static TreeEditor.Web.Tests.Cache.LocalCacheEditTests;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheDeleteTests
{
    [Fact]
    public async Task Delete_marks_the_element_and_every_cached_descendant_even_below_placeholders()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A2"], n["A3"], n["Gone"], n["Beta"]);
        var callsBefore = api.Calls.Count;
        var changes = 0;
        cache.Changed += () => changes++;

        cache.Delete(n["Alpha"].Id);

        Assert.Equal(
            [
                "Root",
                "  Alpha [deleted] [pending]",
                "    Gone [deleted]",
                "    … 1 (A1)",
                "      A2 [deleted] [pending]",
                "        A3 [deleted] [pending]",
                "  Beta",
            ],
            Outline.Of(cache.ViewTree, tree));
        Assert.True(cache.HasPendingChanges);
        // Already deleted in the database: nothing to do for it.
        Assert.Equal(ElementState.Clean, cache.Find(n["Gone"].Id)?.State);
        Assert.Equal(n["A2"].Version, cache.Find(n["A2"].Id)?.Version);
        Assert.Equal(callsBefore, api.Calls.Count);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Delete_drops_the_pending_edits_of_the_element_and_its_descendants()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A2"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Alpha edited");
        cache.EditValue(n["A2"].Id, "A2 edited");
        cache.EditValue(n["Beta"].Id, "Beta edited");

        cache.Delete(n["Alpha"].Id);

        Assert.Equal(
            [
                "Root",
                "  Alpha [deleted] [pending]",
                "    … 1 (A1)",
                "      A2 [deleted] [pending]",
                "  Beta edited [edited]",
            ],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task An_edited_then_deleted_element_sends_only_the_delete_with_its_loaded_version()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A2"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Alpha edited");
        cache.EditValue(n["A2"].Id, "A2 edited");
        cache.EditValue(n["Beta"].Id, "Beta edited");
        cache.Delete(n["Alpha"].Id);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(api.ApplyRequests);
        Assert.Empty(request.Inserts);
        Assert.Equal([new NodeEdit(n["Beta"].Id, "Beta edited", n["Beta"].Version)], request.Edits);
        Assert.Equal([new NodeDelete(n["Alpha"].Id, n["Alpha"].Version)], request.Deletes);
    }

    [Fact]
    public async Task Apply_sends_only_the_topmost_deletes_in_delete_order()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A1"], n["A2"], n["Beta"]);
        // The server's cascade covers the descendants, so they aren't sent even when deleted first.
        cache.Delete(n["A2"].Id);
        cache.Delete(n["Beta"].Id);
        cache.Delete(n["Alpha"].Id);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(api.ApplyRequests);
        Assert.Empty(request.Edits);
        Assert.Equal(
            [new NodeDelete(n["Beta"].Id, n["Beta"].Version), new NodeDelete(n["Alpha"].Id, n["Alpha"].Version)],
            request.Deletes);
    }

    [Fact]
    public async Task Deleted_and_uncached_elements_cannot_be_deleted_or_edited()
    {
        var (tree, n) = Subtree();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"], n["A1"], n["Gone"]);
        cache.Delete(n["Alpha"].Id);

        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["Alpha"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["A1"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["A1"].Id, "Renamed"));
        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["Gone"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["Beta"].Id));
    }

    [Fact]
    public async Task Delete_waits_for_an_apply_in_flight()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta edited");
        var response = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => response.Task);
        var apply = cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["Alpha"].Id));

        response.SetResult(new ApplyResponse([new AppliedNode(n["Beta"].Id, "Beta edited", 5000, IsDeleted: false)]));
        await apply;
        Assert.False(cache.Find(n["Alpha"].Id)?.IsDeleted);
    }

    [Fact]
    public async Task Discard_all_brings_deleted_elements_back_as_loaded()
    {
        var (tree, n) = Subtree();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"], n["Alpha"], n["A2"], n["Gone"]);
        cache.EditValue(n["A2"].Id, "A2 edited");
        cache.Delete(n["Alpha"].Id);

        cache.DiscardAll();

        Assert.False(cache.HasPendingChanges);
        Assert.Equal(
            ["Root", "  Alpha", "    Gone [deleted]", "    … 1 (A1)", "      A2"],
            Outline.Of(cache.ViewTree, tree));
        Assert.Equal(CachedElement.From(n["A2"]), cache.Find(n["A2"].Id));
    }

    [Fact]
    public async Task Element_loaded_below_a_pending_deleted_ancestor_is_deleted_at_once()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"]);
        cache.Delete(n["Alpha"].Id);

        await cache.LoadElementAsync(n["A2"].Id, TestContext.Current.CancellationToken);
        await cache.LoadElementAsync(n["Gone"].Id, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Root", "  Alpha [deleted] [pending]", "    Gone [deleted]", "    … 1 (A1)", "      A2 [deleted] [pending]"],
            Outline.Of(cache.ViewTree, tree));
        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["A2"].Id, "Renamed"));

        // It was never deleted by itself, so it comes back with the discard.
        cache.DiscardAll();
        Assert.Equal(CachedElement.From(n["A2"]), cache.Find(n["A2"].Id));
    }

    [Fact]
    public async Task Element_loaded_below_a_pending_deleted_ancestor_is_not_sent()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.Delete(n["Alpha"].Id);
        await cache.LoadElementAsync(n["A2"].Id, TestContext.Current.CancellationToken);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new NodeDelete(n["Alpha"].Id, n["Alpha"].Version)], Assert.Single(api.ApplyRequests).Deletes);
    }

    [Fact]
    public async Task Successful_apply_leaves_the_deleted_subtree_deleted_with_nothing_pending()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A2"], n["Beta"]);
        cache.Delete(n["Alpha"].Id);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Alpha", 5000, IsDeleted: true)])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(cache.HasPendingChanges);
        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Version = 5000, IsDeleted = true },
            cache.Find(n["Alpha"].Id));
        Assert.Equal(CachedElement.From(n["A2"]) with { IsDeleted = true }, cache.Find(n["A2"].Id));
        Assert.Equal(
            ["Root", "  Alpha [deleted]", "    … 1 (A1)", "      A2 [deleted]", "  Beta"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Successful_apply_marks_cached_descendants_of_elements_the_server_deleted()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["A2"], n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta edited");
        // A response that deletes more than was sent still leaves no live element below a deleted one.
        api.OnApply(_ => Task.FromResult(new ApplyResponse(
            [
                new AppliedNode(n["Beta"].Id, "Beta edited", 5000, IsDeleted: false),
                new AppliedNode(n["Alpha"].Id, "Alpha", 5001, IsDeleted: true),
            ])));

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Root", "  Alpha [deleted]", "    … 1 (A1)", "      A2 [deleted]", "  Beta edited"],
            Outline.Of(cache.ViewTree, tree));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Element_read_before_the_delete_was_applied_arrives_deleted()
    {
        var (tree, n) = Subtree();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"]);
        var release = api.HoldNextLoad();
        var load = cache.LoadElementAsync(n["A2"].Id, TestContext.Current.CancellationToken);
        cache.Delete(n["Alpha"].Id);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Alpha", 5000, IsDeleted: true)])));
        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        release.SetResult();
        await load;

        // Read live before the cascade committed; a deleted ancestor means it is deleted in the database now.
        Assert.Equal(CachedElement.From(n["A2"]) with { IsDeleted = true }, cache.Find(n["A2"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    /// <summary>
    /// Root with Alpha and Beta; below Alpha a chain A1 → A2 → A3 and Gone, deleted in the database.
    /// </summary>
    private static (TestTree Tree, Dictionary<string, NodeDetails> Nodes) Subtree()
    {
        var tree = new TestTree();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var chain = tree.Chain(alpha, "A", firstLevel: 1, count: 3);
        var gone = tree.Child(alpha, "Gone", deleted: true);
        var beta = tree.Child(root, "Beta");
        return (tree, new Dictionary<string, NodeDetails>
        {
            ["Root"] = root,
            ["Alpha"] = alpha,
            ["A1"] = chain[0],
            ["A2"] = chain[1],
            ["A3"] = chain[2],
            ["Gone"] = gone,
            ["Beta"] = beta,
        });
    }
}
