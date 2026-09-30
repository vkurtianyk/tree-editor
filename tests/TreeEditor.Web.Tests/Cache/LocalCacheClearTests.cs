using TreeEditor.Contracts;
using TreeEditor.Web.Cache;
using static TreeEditor.Web.Tests.Cache.LocalCacheEditTests;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheClearTests
{
    [Fact]
    public async Task Clear_empties_the_cache()
    {
        var (tree, root, leaf, other) = Sample();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var element in new[] { leaf, root, other })
        {
            await cache.LoadElementAsync(element.Id, cancellationToken);
        }

        Assert.NotEmpty(cache.ViewTree);

        cache.Clear();

        Assert.Empty(cache.ViewTree);
        Assert.False(cache.IsCached(root.Id));
        Assert.False(cache.IsCached(leaf.Id));
        Assert.False(cache.IsCached(other.Id));
    }

    [Fact]
    public async Task Clear_notifies_a_change()
    {
        var (tree, _, leaf, _) = Sample();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        await cache.LoadElementAsync(leaf.Id, TestContext.Current.CancellationToken);
        var changes = 0;
        cache.Changed += () => changes++;

        cache.Clear();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Clear_reaches_no_api_and_a_cleared_element_loads_again()
    {
        var (tree, root, leaf, _) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = new LocalCache(api);
        var cancellationToken = TestContext.Current.CancellationToken;
        await cache.LoadElementAsync(leaf.Id, cancellationToken);

        cache.Clear();
        Assert.Equal<ApiCall>([new LoadCall(leaf.Id)], api.Calls);

        await cache.LoadElementAsync(leaf.Id, cancellationToken);

        Assert.Equal<ApiCall>([new LoadCall(leaf.Id), new LoadCall(leaf.Id)], api.Calls);
        Assert.Equal(["… 2 (Root..Middle)", "  Leaf"], Outline.Of(cache.ViewTree, tree));
        Assert.False(cache.IsCached(root.Id));
    }

    [Fact]
    public async Task Load_that_finishes_after_a_clear_caches_nothing()
    {
        var (tree, _, leaf, _) = Sample();
        var api = new HeldLoadApiClient();
        var cache = new LocalCache(api);
        var changes = 0;
        cache.Changed += () => changes++;

        var load = cache.LoadElementAsync(leaf.Id, TestContext.Current.CancellationToken);
        cache.Clear();
        Assert.True(tree.TryGet(leaf.Id, out var node));
        api.Release(node);
        await load;

        // The element was read before the clear (a Reset), so it may be stale: it is dropped.
        Assert.False(cache.IsCached(leaf.Id));
        Assert.Empty(cache.ViewTree);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Clear_drops_pending_edits()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");

        cache.Clear();

        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.CanApply);
        await cache.LoadElementAsync(n["Alpha"].Id, cancellationToken);
        Assert.Equal(CachedElement.From(n["Alpha"]), cache.Find(n["Alpha"].Id));
        Assert.False(await cache.ApplyAsync(cancellationToken));
        Assert.Empty(api.ApplyRequests);
    }

    [Fact]
    public async Task Apply_that_finishes_after_a_clear_stores_nothing()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        var response = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => response.Task);
        var apply = cache.ApplyAsync(cancellationToken);

        cache.Clear();
        Assert.False(cache.IsApplying);
        await cache.LoadElementAsync(n["Alpha"].Id, cancellationToken);
        response.SetResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Gamma", 5000, IsDeleted: false)]));
        var stored = await apply;

        // The response answers a request sent before the clear (a Reset): the element loaded since keeps what it read.
        Assert.False(stored);
        Assert.Equal(CachedElement.From(n["Alpha"]), cache.Find(n["Alpha"].Id));
        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.IsApplying);
    }

    [Fact]
    public async Task Apply_that_finishes_after_a_clear_leaves_a_later_apply_in_flight()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        var first = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => first.Task);
        var firstApply = cache.ApplyAsync(cancellationToken);
        cache.Clear();
        await cache.LoadElementAsync(n["Alpha"].Id, cancellationToken);
        cache.EditValue(n["Alpha"].Id, "Omega");
        var second = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => second.Task);
        var secondApply = cache.ApplyAsync(cancellationToken);

        first.SetResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Gamma", 5000, IsDeleted: false)]));
        Assert.False(await firstApply);

        Assert.True(cache.IsApplying);
        Assert.Equal(ElementState.Edited, cache.Find(n["Alpha"].Id)?.State);
        second.SetResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Omega", 5001, IsDeleted: false)]));
        Assert.True(await secondApply);
        Assert.False(cache.IsApplying);
        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Value = "Omega", Version = 5001 },
            cache.Find(n["Alpha"].Id));
    }

    /// <summary>
    /// Root ─ Middle ─ Leaf
    /// Other
    /// </summary>
    private static (TestTree Tree, NodeDetails Root, NodeDetails Leaf, NodeDetails Other) Sample()
    {
        var tree = new TestTree();
        var root = tree.Root("Root");
        var leaf = tree.Child(tree.Child(root, "Middle"), "Leaf");
        var other = tree.Root("Other");
        return (tree, root, leaf, other);
    }

    /// <summary>Holds a load open until the test releases it.</summary>
    private sealed class HeldLoadApiClient : ICacheApiClient
    {
        private readonly TaskCompletionSource<NodeDetails> load = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(NodeDetails node) => load.SetResult(node);

        public Task<NodeDetails> LoadNodeAsync(Guid id, CancellationToken cancellationToken = default) => load.Task;

        public Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ApplyResponse>(new NotSupportedException("No Apply outcome is scripted."));
    }
}
