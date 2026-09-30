using TreeEditor.Contracts;
using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheLoadTests
{
    public static TheoryData<string[]> LoadOrders => new()
    {
        { ["E", "G", "A"] },
        { ["E", "A", "G"] },
        { ["G", "E", "A"] },
        { ["G", "A", "E"] },
        { ["A", "E", "G"] },
        { ["A", "G", "E"] },
    };

    [Theory]
    [MemberData(nameof(LoadOrders))]
    public async Task Any_load_order_gives_the_same_hierarchy(string[] order)
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));

        foreach (var name in order)
        {
            await cache.LoadElementAsync(n[name].Id, TestContext.Current.CancellationToken);
        }

        string[] expected =
        [
            "… 1 (R)",
            "  A",
            "    … 1 (B)",
            "      … 1 (C)",
            "        … 1 (D)",
            "          E",
            "        … 1 (F)",
            "          G",
        ];
        Assert.Equal(expected, Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Element_without_cached_ancestors_sits_under_one_placeholder_counting_them()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));

        await cache.LoadElementAsync(n["E"].Id, TestContext.Current.CancellationToken);

        Assert.Equal(["… 5 (R..D)", "  E"], Outline.Of(cache.ViewTree, tree));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deep_gap_between_a_root_and_a_leaf_collapses_into_one_counted_row(bool rootFirst)
    {
        var tree = new TestTree();
        var root = tree.Root("L1");
        var leaf = tree.Chain(root, "L", 2, 199)[^1];
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(rootFirst ? root.Id : leaf.Id, cancellationToken);
        await cache.LoadElementAsync(rootFirst ? leaf.Id : root.Id, cancellationToken);

        Assert.Equal(["L1", "  … 198 (L2..L199)", "    L200"], Outline.Of(cache.ViewTree, tree));
        var gap = Assert.IsType<PlaceholderRow>(Assert.Single(Assert.Single(cache.ViewTree).Children));
        Assert.Equal(198, gap.MissingLevels);
    }

    [Fact]
    public async Task Loading_an_ancestor_inside_a_gap_splits_the_row_around_it()
    {
        var tree = new TestTree();
        var root = tree.Root("L1");
        var line = tree.Chain(root, "L", 2, 5);
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(line[^1].Id, cancellationToken);
        Assert.Equal(["… 5 (L1..L5)", "  L6"], Outline.Of(cache.ViewTree, tree));

        await cache.LoadElementAsync(line[1].Id, cancellationToken);
        Assert.Equal(
            ["… 2 (L1..L2)", "  L3", "    … 2 (L4..L5)", "      L6"],
            Outline.Of(cache.ViewTree, tree));

        await cache.LoadElementAsync(line[2].Id, cancellationToken);
        Assert.Equal(
            ["… 2 (L1..L2)", "  L3", "    L4", "      … 1 (L5)", "        L6"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Loaded_element_replaces_its_placeholder_at_either_end_of_a_gap()
    {
        var tree = new TestTree();
        var root = tree.Root("L1");
        var line = tree.Chain(root, "L", 2, 5);
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;
        await cache.LoadElementAsync(line[^1].Id, cancellationToken);

        await cache.LoadElementAsync(line[^2].Id, cancellationToken);
        Assert.Equal(["… 4 (L1..L4)", "  L5", "    L6"], Outline.Of(cache.ViewTree, tree));

        await cache.LoadElementAsync(root.Id, cancellationToken);
        Assert.Equal(["L1", "  … 3 (L2..L4)", "    L5", "      L6"], Outline.Of(cache.ViewTree, tree));

        foreach (var element in line)
        {
            await cache.LoadElementAsync(element.Id, cancellationToken);
        }

        Assert.Equal(
            ["L1", "  L2", "    L3", "      L4", "        L5", "          L6"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Branches_below_a_missing_ancestor_share_its_own_placeholder_row()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(n["E"].Id, cancellationToken);
        await cache.LoadElementAsync(n["G"].Id, cancellationToken);

        // The line above the branch point collapses; the branch point C stays a row of its own.
        string[] expected =
        [
            "… 3 (R..B)",
            "  … 1 (C)",
            "    … 1 (D)",
            "      E",
            "    … 1 (F)",
            "      G",
        ];
        Assert.Equal(expected, Outline.Of(cache.ViewTree, tree));
        var shared = Assert.IsType<PlaceholderRow>(Assert.Single(Assert.Single(cache.ViewTree).Children));
        Assert.Equal([n["C"].Id], shared.MissingIds);
        Assert.Equal(2, shared.Children.Count);
    }

    [Fact]
    public async Task Missing_root_where_branches_diverge_is_shared_too()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(n["I"].Id, cancellationToken);
        await cache.LoadElementAsync(n["E"].Id, cancellationToken);

        Assert.Equal(
            ["… 1 (R)", "  … 4 (A..D)", "    E", "  … 1 (H)", "    I"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Loading_the_shared_ancestor_replaces_its_placeholder()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;
        await cache.LoadElementAsync(n["E"].Id, cancellationToken);
        await cache.LoadElementAsync(n["G"].Id, cancellationToken);

        await cache.LoadElementAsync(n["C"].Id, cancellationToken);

        Assert.Equal(
            ["… 3 (R..B)", "  C", "    … 1 (D)", "      E", "    … 1 (F)", "      G"],
            Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Orders_elements_case_insensitively_with_an_id_tiebreak_then_placeholders_by_missing_id()
    {
        var tree = new TestTree();
        var parent = tree.Root("P");
        // Ids ascend in creation order: each pair below is created so that only the intended rule orders it.
        var zulu = tree.Child(parent, "Zulu");
        var zuluChild = tree.Child(zulu, "Z1");
        var mike = tree.Child(parent, "Mike");
        var mikeChild = tree.Child(mike, "M1");
        var cherry = tree.Child(parent, "Cherry");
        var banana = tree.Child(parent, "banana");
        var lowerApple = tree.Child(parent, "apple");
        var upperApple = tree.Child(parent, "Apple");
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var element in new[] { mikeChild, upperApple, cherry, zuluChild, lowerApple, banana, parent })
        {
            await cache.LoadElementAsync(element.Id, cancellationToken);
        }

        string[] expected =
        [
            "P",
            "  apple",
            "  Apple",
            "  banana",
            "  Cherry",
            "  … 1 (Zulu)",
            "    Z1",
            "  … 1 (Mike)",
            "    M1",
        ];
        Assert.Equal(expected, Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Cached_element_keeps_what_load_node_returned()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));

        await cache.LoadElementAsync(n["E"].Id, TestContext.Current.CancellationToken);

        var row = Assert.IsType<CachedElementRow>(Assert.Single(Assert.Single(cache.ViewTree).Children));
        var loaded = n["E"];
        Assert.Equal(loaded.Id, row.Element.Id);
        Assert.Equal(n["D"].Id, row.Element.ParentId);
        Assert.Equal(loaded.Ancestors, row.Element.Ancestors);
        Assert.Equal("E", row.Element.Value);
        Assert.Equal(loaded.Version, row.Element.Version);
        Assert.False(row.Element.IsDeleted);
    }

    [Fact]
    public async Task Deleted_element_is_cached_with_its_deleted_flag()
    {
        var tree = new TestTree();
        var root = tree.Root("Root");
        var gone = tree.Child(root, "Gone", deleted: true);
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(root.Id, cancellationToken);
        await cache.LoadElementAsync(gone.Id, cancellationToken);

        Assert.Equal(["Root", "  Gone [deleted]"], Outline.Of(cache.ViewTree, tree));
    }

    [Fact]
    public async Task Knows_which_ids_are_cached()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        Assert.False(cache.IsCached(n["E"].Id));

        await cache.LoadElementAsync(n["E"].Id, TestContext.Current.CancellationToken);

        Assert.True(cache.IsCached(n["E"].Id));
        Assert.False(cache.IsCached(n["D"].Id));
        Assert.False(cache.IsCached(n["G"].Id));
    }

    [Fact]
    public async Task Notifies_a_change_for_each_element_it_adds()
    {
        var (tree, n) = Branching();
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var changes = 0;
        cache.Changed += () => changes++;
        var cancellationToken = TestContext.Current.CancellationToken;

        await cache.LoadElementAsync(n["E"].Id, cancellationToken);
        Assert.Equal(1, changes);

        await cache.LoadElementAsync(n["E"].Id, cancellationToken);
        Assert.Equal(1, changes);

        await cache.LoadElementAsync(n["G"].Id, cancellationToken);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Only_load_calls_reach_the_api_and_a_cached_element_is_not_loaded_again()
    {
        var (tree, n) = Branching();
        var api = new FakeCacheApiClient(tree);
        var cache = new LocalCache(api);
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var name in new[] { "E", "G", "E", "A", "G" })
        {
            await cache.LoadElementAsync(n[name].Id, cancellationToken);
        }

        _ = cache.ViewTree;
        _ = cache.IsCached(n["A"].Id);

        Assert.Equal<ApiCall>(
            [new LoadCall(n["E"].Id), new LoadCall(n["G"].Id), new LoadCall(n["A"].Id)],
            api.Calls);
    }

    [Fact]
    public async Task Failed_load_caches_nothing_and_reports_the_error()
    {
        var tree = new TestTree();
        tree.Root("Root");
        var cache = new LocalCache(new FakeCacheApiClient(tree));
        var changes = 0;
        cache.Changed += () => changes++;
        var unknownId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => cache.LoadElementAsync(unknownId, TestContext.Current.CancellationToken));

        Assert.False(cache.IsCached(unknownId));
        Assert.Empty(cache.ViewTree);
        Assert.Equal(0, changes);
    }

    /// <summary>
    /// R ─ A ─ B ─ C ─ D ─ E
    ///             └─ F ─ G
    /// R ─ H ─ I
    /// </summary>
    private static (TestTree Tree, Dictionary<string, NodeDetails> Nodes) Branching()
    {
        var tree = new TestTree();
        var r = tree.Root("R");
        var a = tree.Child(r, "A");
        var b = tree.Child(a, "B");
        var c = tree.Child(b, "C");
        var d = tree.Child(c, "D");
        var e = tree.Child(d, "E");
        var f = tree.Child(c, "F");
        var g = tree.Child(f, "G");
        var h = tree.Child(r, "H");
        var i = tree.Child(h, "I");
        NodeDetails[] all = [r, a, b, c, d, e, f, g, h, i];
        return (tree, all.ToDictionary(node => node.Value));
    }
}
