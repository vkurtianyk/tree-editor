using System.Net.Http.Json;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Nodes;

public sealed class ListChildrenTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Without_parent_lists_the_roots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var beta = tree.Root("beta");
        var alpha = tree.Root("Alpha");
        tree.Child(alpha, "Child of Alpha");
        var gamma = tree.Root("Gamma", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);

        var page = await ListAsync(api, parentId: null, after: null, cancellationToken);

        NodeListItem[] expected =
        [
            new(alpha.Id, "Alpha", IsDeleted: false, HasChildren: true),
            new(beta.Id, "beta", IsDeleted: false, HasChildren: false),
            new(gamma.Id, "Gamma", IsDeleted: true, HasChildren: false),
        ];
        Assert.Equal(expected, page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task Orders_children_case_insensitively_with_an_id_tiebreak_among_live_and_deleted_namesakes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var parent = tree.Root("Fruit");
        var cherry = tree.Child(parent, "Cherry");
        // Namesakes are inserted in descending id order, so only the id tiebreak gives the expected order.
        var ids = OrderedIds(3);
        var bananaHighId = tree.Child(parent, "banana", deleted: true, id: ids[2]);
        var bananaMidId = tree.Child(parent, "Banana", id: ids[1]);
        var bananaLowId = tree.Child(parent, "BANANA", deleted: true, id: ids[0]);
        var apple = tree.Child(parent, "apple");
        await api.ArrangeAsync(tree, cancellationToken);

        var page = await ListAsync(api, parent.Id, after: null, cancellationToken);

        NodeListItem[] expected =
        [
            new(apple.Id, "apple", IsDeleted: false, HasChildren: false),
            new(bananaLowId.Id, "BANANA", IsDeleted: true, HasChildren: false),
            new(bananaMidId.Id, "Banana", IsDeleted: false, HasChildren: false),
            new(bananaHighId.Id, "banana", IsDeleted: true, HasChildren: false),
            new(cherry.Id, "Cherry", IsDeleted: false, HasChildren: false),
        ];
        Assert.Equal(expected, page.Items);
    }

    [Fact]
    public async Task Pages_through_a_wide_parent_without_skipping_or_repeating_a_child()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var parent = tree.Root("Wide");
        // 84 groups of three namesakes (one live, two deleted): the page ends at 100 and 200 fall inside a group.
        // The values carry URL-special characters, so the cursor has to travel escaped.
        var children = new List<(int Group, Node Node)>();
        for (var group = 0; group < 84; group++)
        {
            children.Add((group, tree.Child(parent, $"Item {group:000} #1+1=2 & 50%?")));
            children.Add((group, tree.Child(parent, $"item {group:000} #1+1=2 & 50%?", deleted: true)));
            children.Add((group, tree.Child(parent, $"ITEM {group:000} #1+1=2 & 50%?", deleted: true)));
        }

        await api.ArrangeAsync(tree, cancellationToken);

        var pages = new List<ChildrenPage>();
        ChildrenCursor? after = null;
        do
        {
            var page = await ListAsync(api, parent.Id, after, cancellationToken);
            pages.Add(page);
            after = page.Next;
        }
        while (after is not null && pages.Count < 10);

        Assert.Equal([100, 100, 52], pages.Select(page => page.Items.Count));
        Assert.Equal([true, true, false], pages.Select(page => page.HasMore));
        var expectedIds = children
            .OrderBy(child => child.Group)
            .ThenBy(child => child.Node.Id, PostgresUuidOrder)
            .Select(child => child.Node.Id);
        Assert.Equal(expectedIds, pages.SelectMany(page => page.Items).Select(item => item.Id));
    }

    [Theory]
    [InlineData(ChildrenPage.PageSize, false)]
    [InlineData(ChildrenPage.PageSize + 1, true)]
    public async Task Has_more_only_when_a_child_exists_beyond_the_page(int childCount, bool hasMore)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var parent = tree.Root("Parent");
        for (var i = 0; i < childCount; i++)
        {
            tree.Child(parent, $"Child {i:000}");
        }

        await api.ArrangeAsync(tree, cancellationToken);

        var page = await ListAsync(api, parent.Id, after: null, cancellationToken);

        Assert.Equal(ChildrenPage.PageSize, page.Items.Count);
        Assert.Equal(hasMore, page.HasMore);
        var last = page.Items[^1];
        Assert.Equal(hasMore ? new ChildrenCursor(last.Value.ToLowerInvariant(), last.Id) : null, page.Next);
    }

    [Fact]
    public async Task Lists_deleted_children_and_counts_deleted_grandchildren_in_hasChildren()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var parent = tree.Root("Parent");
        var liveWithDeletedChild = tree.Child(parent, "A live, deleted child");
        tree.Child(liveWithDeletedChild, "Deleted grandchild", deleted: true);
        var deletedWithDeletedChild = tree.Child(parent, "B deleted, deleted child", deleted: true);
        tree.Child(deletedWithDeletedChild, "Deleted grandchild", deleted: true);
        var liveLeaf = tree.Child(parent, "C live leaf");
        var deletedLeaf = tree.Child(parent, "D deleted leaf", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);

        var page = await ListAsync(api, parent.Id, after: null, cancellationToken);

        NodeListItem[] expected =
        [
            new(liveWithDeletedChild.Id, "A live, deleted child", IsDeleted: false, HasChildren: true),
            new(deletedWithDeletedChild.Id, "B deleted, deleted child", IsDeleted: true, HasChildren: true),
            new(liveLeaf.Id, "C live leaf", IsDeleted: false, HasChildren: false),
            new(deletedLeaf.Id, "D deleted leaf", IsDeleted: true, HasChildren: false),
        ];
        Assert.Equal(expected, page.Items);
    }

    /// <summary>PostgreSQL orders uuids by their bytes, which is the order of their text form.</summary>
    private static readonly Comparer<Guid> PostgresUuidOrder =
        Comparer<Guid>.Create((x, y) => string.CompareOrdinal(x.ToString(), y.ToString()));

    private static Guid[] OrderedIds(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).Order(PostgresUuidOrder)];

    private static async Task<ChildrenPage> ListAsync(
        ApiHarness api, Guid? parentId, ChildrenCursor? after, CancellationToken cancellationToken)
    {
        var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId, after), cancellationToken);
        Assert.NotNull(page);
        return page;
    }
}
