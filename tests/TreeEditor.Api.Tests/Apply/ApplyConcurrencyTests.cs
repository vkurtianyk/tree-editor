using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TreeEditor.Api.Apply;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Apply;

/// <summary>
/// Applies racing each other. The test holds the lock the Applies need, waits until they all queue up for it, then
/// releases it: Postgres grants a lock to its waiters in queue order, so the race always runs the same way.
/// </summary>
public sealed class ApplyConcurrencyTests(PostgresFixture postgres)
{
    /// <summary>How long a request that must not be blocked gets to finish while the test holds a lock.</summary>
    private static readonly TimeSpan UnblockedTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_subtree_delete_racing_an_insert_inside_it_leaves_no_live_element_under_a_deleted_one(bool deleteFirst)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var child = tree.Child(alpha, "Child");
        await api.ArrangeAsync(tree, cancellationToken);
        var newId = Guid.CreateVersion7();
        var delete = new ApplyRequest([], [], [new NodeDelete(alpha.Id, alpha.Version)]);
        var insert = new ApplyRequest([new NodeInsert(newId, child.Id, "New")], [], []);

        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.KeyOf(root.Id), cancellationToken);
        var applies = await QueueInOrderAsync(api, held, deleteFirst ? [delete, insert] : [insert, delete], cancellationToken);
        var (deleteApply, insertApply) = deleteFirst ? (applies[0], applies[1]) : (applies[1], applies[0]);
        await held.ReleaseAsync(cancellationToken);
        using var deleteResponse = await deleteApply;
        using var insertResponse = await insertApply;

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        if (deleteFirst)
        {
            // The insert sees its parent deleted.
            Assert.Equal(HttpStatusCode.Conflict, insertResponse.StatusCode);
            await AssertNotFoundAsync(api, newId, cancellationToken);
        }
        else
        {
            // The delete's cascade reaches the element inserted just before it.
            Assert.Equal(HttpStatusCode.OK, insertResponse.StatusCode);
            Assert.True((await LoadAsync(api, newId, cancellationToken)).IsDeleted);
        }

        Assert.True((await LoadAsync(api, child.Id, cancellationToken)).IsDeleted);
        await AssertNoLiveElementUnderADeletedOneAsync(api, cancellationToken);
    }

    [Fact]
    public async Task Inserts_of_the_same_name_under_one_parent_racing_both_succeed_with_distinct_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var parent = tree.Child(root, "Parent");
        await api.ArrangeAsync(tree, cancellationToken);
        var (firstId, secondId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.KeyOf(root.Id), cancellationToken);
        var applies = await QueueInOrderAsync(
            api,
            held,
            [
                new ApplyRequest([new NodeInsert(firstId, parent.Id, "New")], [], []),
                new ApplyRequest([new NodeInsert(secondId, parent.Id, "New")], [], []),
            ],
            cancellationToken);
        await held.ReleaseAsync(cancellationToken);

        var first = Assert.Single((await ReadOkAsync(applies[0], cancellationToken)).Nodes);
        var second = Assert.Single((await ReadOkAsync(applies[1], cancellationToken)).Nodes);
        Assert.Equal((firstId, "New"), (first.Id, first.Value));
        Assert.Equal((secondId, "New (1)"), (second.Id, second.Value));
        Assert.Equal(["New", "New (1)"], (await LiveChildrenAsync(api, parent.Id, cancellationToken)).Order());
    }

    [Fact]
    public async Task An_Apply_on_another_root_tree_is_not_blocked_by_an_Apply_holding_this_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var home = tree.Root("Home");
        var lamp = tree.Child(home, "Lamp");
        var garden = tree.Root("Garden");
        var hose = tree.Child(garden, "Hose");
        await api.ArrangeAsync(tree, cancellationToken);

        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.KeyOf(home.Id), cancellationToken);
        var homeApplies = await QueueInOrderAsync(
            api, held, [Edit(lamp.Id, "Desk lamp", lamp.Version)], cancellationToken);

        var gardenApply = ApplyAsync(api, Edit(hose.Id, "Garden hose", hose.Version), cancellationToken);
        var gardenApplied = await ReadOkAsync(gardenApply.WaitAsync(UnblockedTimeout, cancellationToken), cancellationToken);
        Assert.Equal("Garden hose", Assert.Single(gardenApplied.Nodes).Value);
        Assert.False(homeApplies[0].IsCompleted);

        await held.ReleaseAsync(cancellationToken);
        Assert.Equal("Desk lamp", Assert.Single((await ReadOkAsync(homeApplies[0], cancellationToken)).Nodes).Value);
    }

    [Fact]
    public async Task A_root_rename_waits_for_the_roots_lock_but_an_edit_below_a_root_does_not()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        var beta = tree.Root("Beta");
        var betaChild = tree.Child(beta, "Beta child");
        await api.ArrangeAsync(tree, cancellationToken);

        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.RootsKey, cancellationToken);
        var renames = await QueueInOrderAsync(api, held, [Edit(alpha.Id, "Alpha renamed", alpha.Version)], cancellationToken);

        var childApply = ApplyAsync(api, Edit(betaChild.Id, "Child renamed", betaChild.Version), cancellationToken);
        var childApplied = await ReadOkAsync(childApply.WaitAsync(UnblockedTimeout, cancellationToken), cancellationToken);
        Assert.Equal("Child renamed", Assert.Single(childApplied.Nodes).Value);
        Assert.False(renames[0].IsCompleted);

        await held.ReleaseAsync(cancellationToken);
        Assert.Equal("Alpha renamed", Assert.Single((await ReadOkAsync(renames[0], cancellationToken)).Nodes).Value);
    }

    [Fact]
    public async Task Roots_of_different_trees_renamed_to_the_same_name_racing_both_succeed_with_distinct_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        var beta = tree.Root("Beta");
        await api.ArrangeAsync(tree, cancellationToken);

        // Each rename also locks its own root, a different one; only the roots lock makes them wait for each other.
        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.RootsKey, cancellationToken);
        var renames = await QueueInOrderAsync(
            api,
            held,
            [Edit(alpha.Id, "Gamma", alpha.Version), Edit(beta.Id, "Gamma", beta.Version)],
            cancellationToken);
        await held.ReleaseAsync(cancellationToken);

        Assert.Equal("Gamma", Assert.Single((await ReadOkAsync(renames[0], cancellationToken)).Nodes).Value);
        Assert.Equal("Gamma (1)", Assert.Single((await ReadOkAsync(renames[1], cancellationToken)).Nodes).Value);
    }

    [Fact]
    public async Task A_conflicting_Apply_writes_nothing_and_releases_the_trees_lock_for_the_next_Apply()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);
        // Someone else renames Beta after this client loaded it.
        await ReadOkAsync(ApplyAsync(api, Edit(beta.Id, "Beta elsewhere", beta.Version), cancellationToken), cancellationToken);

        // Queued behind the test's lock, so the conflict is found only once the Apply holds the tree's lock.
        await using var held = await HeldAdvisoryLock.TakeAsync(api, ApplyLocks.KeyOf(root.Id), cancellationToken);
        var conflicting = await QueueInOrderAsync(
            api,
            held,
            [new ApplyRequest([], [new NodeEdit(alpha.Id, "Alpha mine", alpha.Version), new NodeEdit(beta.Id, "Beta mine", beta.Version)], [])],
            cancellationToken);
        await held.ReleaseAsync(cancellationToken);
        using (var response = await conflicting[0])
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.Equal(("Alpha", alpha.Version), (alphaNow.Value, alphaNow.Version));
        Assert.Equal("Beta elsewhere", (await LoadAsync(api, beta.Id, cancellationToken)).Value);
        // A lock the conflicting Apply kept would block this one.
        var next = ApplyAsync(api, Edit(alpha.Id, "Alpha next", alpha.Version), cancellationToken);
        var nextApplied = await ReadOkAsync(next.WaitAsync(UnblockedTimeout, cancellationToken), cancellationToken);
        Assert.Equal("Alpha next", Assert.Single(nextApplied.Nodes).Value);
    }

    /// <summary>
    /// Sends the Applies one by one, each only once the previous ones wait for <paramref name="held"/>, so they get
    /// the lock in this order when it's released.
    /// </summary>
    private static async Task<List<Task<HttpResponseMessage>>> QueueInOrderAsync(
        ApiHarness api,
        HeldAdvisoryLock held,
        IReadOnlyList<ApplyRequest> requests,
        CancellationToken cancellationToken)
    {
        var applies = new List<Task<HttpResponseMessage>>(requests.Count);
        foreach (var request in requests)
        {
            applies.Add(ApplyAsync(api, request, cancellationToken));
            await held.WaitUntilQueuedAsync(applies, cancellationToken);
        }

        return applies;
    }

    private static ApplyRequest Edit(Guid id, string value, uint version) => new([], [new NodeEdit(id, value, version)], []);

    private static Task<HttpResponseMessage> ApplyAsync(ApiHarness api, ApplyRequest request, CancellationToken cancellationToken) =>
        api.Client.PostAsJsonAsync(ApiRoutes.Apply, request, cancellationToken);

    private static async Task<ApplyResponse> ReadOkAsync(Task<HttpResponseMessage> apply, CancellationToken cancellationToken)
    {
        using var response = await apply;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>(cancellationToken);
        Assert.NotNull(applied);
        return applied;
    }

    private static async Task<NodeDetails> LoadAsync(ApiHarness api, Guid id, CancellationToken cancellationToken)
    {
        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(id), cancellationToken);
        Assert.NotNull(node);
        return node;
    }

    private static async Task AssertNotFoundAsync(ApiHarness api, Guid id, CancellationToken cancellationToken)
    {
        using var response = await api.Client.GetAsync(ApiRoutes.NodeById(id), cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<IEnumerable<string>> LiveChildrenAsync(ApiHarness api, Guid parentId, CancellationToken cancellationToken)
    {
        var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId), cancellationToken);
        Assert.NotNull(page);
        return page.Items.Where(item => !item.IsDeleted).Select(item => item.Value);
    }

    /// <summary>The tree's invariant, checked on every stored element: a deleted element's whole subtree is deleted.</summary>
    private static async Task AssertNoLiveElementUnderADeletedOneAsync(ApiHarness api, CancellationToken cancellationToken)
    {
        await using var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        var nodes = await db.Nodes.AsNoTracking().ToDictionaryAsync(node => node.Id, cancellationToken);
        Assert.Empty(nodes.Values
            .Where(node => !node.IsDeleted && node.Ancestors.SkipLast(1).Any(ancestorId => nodes[ancestorId].IsDeleted))
            .Select(node => node.Value));
    }
}
