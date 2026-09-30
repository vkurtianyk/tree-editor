using TreeEditor.Contracts;
using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheEditTests
{
    [Fact]
    public async Task Edit_is_pending_and_marked_edited_without_any_request()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        var callsBefore = api.Calls.Count;

        var result = cache.EditValue(n["Alpha"].Id, "  Gamma  ");

        Assert.Equal(new ValueEditResult("Gamma", Error: null), result);
        Assert.True(cache.HasPendingChanges);
        var element = cache.Find(n["Alpha"].Id);
        Assert.NotNull(element);
        Assert.Equal(ElementState.Edited, element.State);
        Assert.Equal(n["Alpha"].Version, element.Version);
        Assert.Equal(["… 1 (Root)", "  Beta", "  Gamma [edited]"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(callsBefore, api.Calls.Count);
    }

    [Theory]
    [InlineData("", "A value is required.")]
    [InlineData("   ", "A value is required.")]
    [InlineData(null, "A value is required.")]
    public async Task Invalid_value_is_reported_at_once_and_changes_nothing(string? value, string error)
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"]);
        var changes = 0;
        cache.Changed += () => changes++;

        var result = cache.EditValue(n["Alpha"].Id, value);

        Assert.Equal(new ValueEditResult(Value: null, error), result);
        Assert.False(cache.HasPendingChanges);
        Assert.Equal(ElementState.Clean, cache.Find(n["Alpha"].Id)?.State);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Too_long_value_is_reported()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"]);

        var result = cache.EditValue(n["Alpha"].Id, new string('x', 256));

        Assert.Equal("A value can have at most 255 characters.", result.Error);
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Value_colliding_with_a_cached_live_sibling_gets_a_local_suffix()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"], n["Beta"], n["Gone"]);

        Assert.Equal("beta (1)", cache.EditValue(n["Alpha"].Id, "beta").Value);
        // Deleted siblings don't collide, and uncached ones are left to the server.
        Assert.Equal("Gone", cache.EditValue(n["Alpha"].Id, "Gone").Value);
        Assert.Equal("Delta", cache.EditValue(n["Alpha"].Id, "Delta").Value);
        // Nor does the element itself.
        Assert.Equal("BETA", cache.EditValue(n["Beta"].Id, "BETA").Value);
    }

    [Fact]
    public async Task Sibling_collisions_use_pending_values()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");

        Assert.Equal("Gamma (1)", cache.EditValue(n["Beta"].Id, "Gamma").Value);
        // Alpha's loaded value is free again.
        Assert.Equal("Alpha", cache.EditValue(n["Beta"].Id, "Alpha").Value);
    }

    [Fact]
    public async Task Root_rename_collides_with_cached_roots_only()
    {
        var (tree, n) = Siblings();
        var other = tree.Root("Other");
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Root"], other, n["Alpha"]);

        Assert.Equal("root (1)", cache.EditValue(other.Id, "root").Value);
        Assert.Equal("Alpha", cache.EditValue(other.Id, "Alpha").Value);
    }

    [Fact]
    public async Task Editing_back_to_the_loaded_value_leaves_nothing_pending()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");

        cache.EditValue(n["Alpha"].Id, " Alpha ");

        Assert.False(cache.HasPendingChanges);
        Assert.Equal(ElementState.Clean, cache.Find(n["Alpha"].Id)?.State);
    }

    [Fact]
    public async Task Edit_notifies_a_change()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"]);
        var changes = 0;
        cache.Changed += () => changes++;

        cache.EditValue(n["Alpha"].Id, "Gamma");

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Deleted_or_uncached_elements_cannot_be_edited()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Gone"]);

        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["Gone"].Id, "Back"));
        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["Alpha"].Id, "Gamma"));
    }

    [Fact]
    public async Task Discard_all_restores_loaded_values_and_keeps_every_cached_element()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"], n["Gone"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        cache.EditValue(n["Beta"].Id, "Delta");
        var changes = 0;
        cache.Changed += () => changes++;
        var callsBefore = api.Calls.Count;

        cache.DiscardAll();

        Assert.False(cache.HasPendingChanges);
        Assert.Equal(["… 1 (Root)", "  Alpha", "  Beta", "  Gone [deleted]"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(CachedElement.From(n["Alpha"]), cache.Find(n["Alpha"].Id));
        Assert.Equal(1, changes);
        Assert.Equal(callsBefore, api.Calls.Count);
    }

    [Fact]
    public async Task Discard_all_with_nothing_pending_changes_nothing()
    {
        var (tree, n) = Siblings();
        var cache = await LoadedAsync(new FakeCacheApiClient(tree), n["Alpha"]);
        var changes = 0;
        cache.Changed += () => changes++;

        cache.DiscardAll();

        Assert.Equal(0, changes);
        Assert.Equal(["… 1 (Root)", "  Alpha"], Outline.Of(cache.ViewTree, tree));
    }

    /// <summary>Root with children Alpha, Beta, Delta and the deleted Gone.</summary>
    internal static (TestTree Tree, Dictionary<string, NodeDetails> Nodes) Siblings()
    {
        var tree = new TestTree();
        var root = tree.Root("Root");
        var nodes = new Dictionary<string, NodeDetails> { ["Root"] = root };
        foreach (var name in new[] { "Alpha", "Beta", "Delta" })
        {
            nodes[name] = tree.Child(root, name);
        }

        nodes["Gone"] = tree.Child(root, "Gone", deleted: true);
        return (tree, nodes);
    }

    internal static async Task<LocalCache> LoadedAsync(FakeCacheApiClient api, params NodeDetails[] nodes)
    {
        var cache = new LocalCache(api);
        foreach (var node in nodes)
        {
            await cache.LoadElementAsync(node.Id, TestContext.Current.CancellationToken);
        }

        return cache;
    }
}
