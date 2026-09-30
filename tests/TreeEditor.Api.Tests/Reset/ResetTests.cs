using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Reset;

public sealed class ResetTests(PostgresFixture postgres)
{
    private static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task After_changes_restores_the_exact_seed_ids_and_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var seed = new TreeBuilder();
        var home = seed.Root("Home");
        var kitchen = seed.Child(home, "Kitchen");
        seed.Child(kitchen, "Knife");
        var old = seed.Child(home, "Old", deleted: true);
        var garden = seed.Root("Garden");
        seed.Child(garden, "Rake");
        await api.ArrangeSeedAsync(seed, cancellationToken);

        // Changes the way Applies would leave them: an edit, a cascaded delete, an undeleted element, additions.
        var added = new TreeBuilder();
        var addedChild = added.Child(kitchen, "Spoon");
        var addedRoot = added.Root("Workshop");
        await api.ArrangeAsync(added, cancellationToken);
        await using (var scope = api.CreateDatabaseScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
            await db.Nodes.Where(n => n.Id == kitchen.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Value, "Kitchen (edited)"), cancellationToken);
            await db.Nodes.Where(n => n.Ancestors.Contains(garden.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsDeleted, true), cancellationToken);
            await db.Nodes.Where(n => n.Id == old.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsDeleted, false), cancellationToken);
        }

        using var response = await api.Client.PostAsync(ApiRoutes.Reset, content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Expected(seed), await WalkAsync(api, cancellationToken));
        foreach (var id in new[] { addedChild.Id, addedRoot.Id })
        {
            using var load = await api.Client.GetAsync(ApiRoutes.NodeById(id), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, load.StatusCode);
        }

        var reloaded = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(kitchen.Id), cancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal(kitchen.Ancestors, reloaded.Ancestors);
        Assert.Equal("Kitchen", reloaded.Value);
    }

    [Fact]
    public async Task Waits_for_a_running_write_transaction_and_then_restores_the_seed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var seed = new TreeBuilder();
        var home = seed.Root("Home");
        seed.Child(home, "Kitchen");
        await api.ArrangeSeedAsync(seed, cancellationToken);

        // Stands in for an Apply in progress: an open transaction that has written to nodes.
        await using var writerScope = api.CreateDatabaseScope();
        var writer = writerScope.ServiceProvider.GetRequiredService<TreeDbContext>();
        await using var apply = await writer.Database.BeginTransactionAsync(cancellationToken);
        await writer.Nodes.Where(n => n.Id == home.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Value, "Home (edited)"), cancellationToken);

        var reset = api.Client.PostAsync(ApiRoutes.Reset, content: null, cancellationToken);
        await WaitUntilTruncateWaitsForALockAsync(api, cancellationToken);
        Assert.False(reset.IsCompleted);

        await apply.CommitAsync(cancellationToken);
        using var response = await reset;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Expected(seed), await WalkAsync(api, cancellationToken));
    }

    private static List<(Guid Id, Guid? ParentId, string Value, bool IsDeleted)> Expected(TreeBuilder seed) =>
        [.. seed.Nodes.Select(n => (n.Id, n.ParentId, n.Value, n.IsDeleted)).OrderBy(n => n.Id)];

    /// <summary>Every element reachable from the roots through list children.</summary>
    private static async Task<List<(Guid Id, Guid? ParentId, string Value, bool IsDeleted)>> WalkAsync(
        ApiHarness api,
        CancellationToken cancellationToken)
    {
        var found = new List<(Guid Id, Guid? ParentId, string Value, bool IsDeleted)>();
        var parents = new Queue<Guid?>([null]);
        while (parents.TryDequeue(out var parentId))
        {
            var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId), cancellationToken);
            Assert.NotNull(page);
            Assert.False(page.HasMore);
            foreach (var item in page.Items)
            {
                found.Add((item.Id, parentId, item.Value, item.IsDeleted));
                if (item.HasChildren)
                {
                    parents.Enqueue(item.Id);
                }
            }
        }

        return [.. found.OrderBy(n => n.Id)];
    }

    private static async Task WaitUntilTruncateWaitsForALockAsync(ApiHarness api, CancellationToken cancellationToken)
    {
        await using var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LockWaitTimeout);
        while (true)
        {
            var waiting = await db.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT count(*)::int AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE 'TRUNCATE%'
                    """)
                .SingleAsync(timeout.Token);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}
