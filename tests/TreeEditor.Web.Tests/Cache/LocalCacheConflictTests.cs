using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Web.Api;
using TreeEditor.Web.Cache;
using static TreeEditor.Web.Tests.Cache.LocalCacheEditTests;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheConflictTests
{
    [Fact]
    public async Task Conflict_keeps_every_pending_change_and_flags_each_conflicting_element()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Root"], n["Alpha"], n["Beta"], n["Gamma"], n["G1"], n["Delta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        cache.EditValue(n["Delta"].Id, "Delta mine");
        cache.Delete(n["Gamma"].Id);
        var kid = cache.AddChild(n["Alpha"].Id, "Kid").Id!.Value;
        var betaConflict = new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false);
        var gammaConflict = new NodeConflict(n["Gamma"].Id, ConflictReason.VersionChanged, "Gamma elsewhere", 2001, false);
        OnApplyConflict(api, betaConflict, gammaConflict);

        var rejected = await ApplyRejectedAsync(cache);

        Assert.Equal([betaConflict, gammaConflict], rejected.Conflicts);
        Assert.False(cache.IsApplying);
        Assert.True(cache.HasPendingChanges);
        Assert.True(cache.HasUnresolvedConflicts);
        Assert.False(cache.CanApply);
        Assert.Equal(
            [
                CachedElement.From(n["Beta"]) with { Value = "Beta mine", State = ElementState.Edited, Conflict = betaConflict },
                CachedElement.From(n["Gamma"]) with { IsDeleted = true, State = ElementState.Deleted, Conflict = gammaConflict },
            ],
            cache.UnresolvedConflicts);
        Assert.Equal(
            [
                "Root",
                "  Alpha",
                "    Kid [new]",
                "  Beta mine [edited] [conflict]",
                "  Delta mine [edited]",
                "  Gamma [deleted] [pending] [conflict]",
                "    G1 [deleted] [pending]",
            ],
            Outline.Of(cache.ViewTree, tree));
        Assert.Equal(ElementState.New, cache.Find(kid)?.State);
    }

    [Fact]
    public async Task Apply_is_disabled_until_every_conflict_is_resolved_and_nothing_is_sent_again_by_itself()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Alpha mine");
        cache.EditValue(n["Beta"].Id, "Beta mine");
        OnApplyConflict(
            api,
            new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "Alpha elsewhere", 2000, false),
            new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2001, false));
        await ApplyRejectedAsync(cache);

        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        cache.TakeDatabase(n["Alpha"].Id);
        Assert.False(cache.CanApply);
        cache.KeepMine(n["Beta"].Id);

        Assert.True(cache.CanApply);
        Assert.False(cache.HasUnresolvedConflicts);
        Assert.Empty(cache.UnresolvedConflicts);
        Assert.Single(api.ApplyRequests);
    }

    [Fact]
    public async Task Elements_deleted_in_the_database_are_deleted_here_with_their_cached_descendants_and_changes()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(
            api, n["Root"], n["Alpha"], n["A1"], n["A2"], n["Beta"], n["Gamma"], n["G1"], n["Delta"]);
        cache.EditValue(n["A1"].Id, "A1 mine");
        var kid = cache.AddChild(n["Alpha"].Id, "Kid").Id!.Value;
        var grandkid = cache.AddChild(kid, "Grandkid").Id!.Value;
        var a2Kid = cache.AddChild(n["A2"].Id, "A2 kid").Id!.Value;
        cache.Delete(n["Gamma"].Id);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        cache.EditValue(n["Delta"].Id, "Delta mine");
        var deltaConflict = new NodeConflict(n["Delta"].Id, ConflictReason.VersionChanged, "Delta elsewhere", 2004, false);
        // Alpha is the insert's parent; A1 and Gamma went with it or on their own.
        OnApplyConflict(
            api,
            new NodeConflict(n["Alpha"].Id, ConflictReason.Deleted, "Alpha", 2001, true),
            new NodeConflict(n["A1"].Id, ConflictReason.Deleted, "A1", 2002, true),
            new NodeConflict(n["Gamma"].Id, ConflictReason.Deleted, "Gamma elsewhere", 2003, true),
            deltaConflict);

        await ApplyRejectedAsync(cache);

        Assert.Equal(CachedElement.From(n["Alpha"]) with { Version = 2001, IsDeleted = true }, cache.Find(n["Alpha"].Id));
        Assert.Equal(CachedElement.From(n["A1"]) with { Version = 2002, IsDeleted = true }, cache.Find(n["A1"].Id));
        Assert.Equal(CachedElement.From(n["A2"]) with { IsDeleted = true }, cache.Find(n["A2"].Id));
        Assert.Equal(
            CachedElement.From(n["Gamma"]) with { Value = "Gamma elsewhere", Version = 2003, IsDeleted = true },
            cache.Find(n["Gamma"].Id));
        Assert.Equal(CachedElement.From(n["G1"]) with { IsDeleted = true }, cache.Find(n["G1"].Id));
        Assert.False(cache.IsCached(kid));
        Assert.False(cache.IsCached(grandkid));
        Assert.False(cache.IsCached(a2Kid));
        Assert.Equal([n["Delta"].Id], cache.UnresolvedConflicts.Select(element => element.Id));
        Assert.Equal(
            [
                "Root",
                "  Alpha [deleted]",
                "    A1 [deleted]",
                "      A2 [deleted]",
                "  Beta mine [edited]",
                "  Delta mine [edited] [conflict]",
                "  Gamma elsewhere [deleted]",
                "    G1 [deleted]",
            ],
            Outline.Of(cache.ViewTree, tree));

        cache.KeepMine(n["Delta"].Id);
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        var request = api.ApplyRequests[^1];
        Assert.Empty(request.Inserts);
        Assert.Equal(
            [new NodeEdit(n["Beta"].Id, "Beta mine", n["Beta"].Version), new NodeEdit(n["Delta"].Id, "Delta mine", 2004)],
            request.Edits);
        Assert.Empty(request.Deletes);
    }

    [Fact]
    public async Task Element_missing_from_the_database_counts_as_deleted_and_keeps_its_loaded_value()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Beta"], n["Delta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        var kid = cache.AddChild(n["Delta"].Id, "Kid").Id!.Value;
        // Another tab's Reset removed both.
        OnApplyConflict(
            api,
            new NodeConflict(n["Delta"].Id, ConflictReason.Deleted, null, null, true),
            new NodeConflict(n["Beta"].Id, ConflictReason.Deleted, null, null, true));

        await ApplyRejectedAsync(cache);

        Assert.Equal(CachedElement.From(n["Beta"]) with { IsDeleted = true }, cache.Find(n["Beta"].Id));
        Assert.Equal(CachedElement.From(n["Delta"]) with { IsDeleted = true }, cache.Find(n["Delta"].Id));
        Assert.False(cache.IsCached(kid));
        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.HasUnresolvedConflicts);
    }

    [Fact]
    public async Task Take_database_stores_its_value_and_version_and_drops_the_edit()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);
        var changes = 0;
        cache.Changed += () => changes++;

        cache.TakeDatabase(n["Beta"].Id);

        Assert.Equal(1, changes);
        Assert.Equal(CachedElement.From(n["Beta"]) with { Value = "Beta elsewhere", Version = 2000 }, cache.Find(n["Beta"].Id));
        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.HasUnresolvedConflicts);

        // The database copy is the new baseline: a later edit is sent with its version.
        cache.EditValue(n["Beta"].Id, "Again");
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new NodeEdit(n["Beta"].Id, "Again", 2000), Assert.Single(api.ApplyRequests[^1].Edits));
    }

    [Fact]
    public async Task Take_database_suffixes_a_cached_sibling_whose_pending_value_it_collides_with()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Alpha mine");
        cache.EditValue(n["Beta"].Id, "Zed");
        OnApplyConflict(api, new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "zed", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.TakeDatabase(n["Alpha"].Id);

        Assert.Equal(CachedElement.From(n["Alpha"]) with { Value = "zed", Version = 2000 }, cache.Find(n["Alpha"].Id));
        Assert.Equal(
            CachedElement.From(n["Beta"]) with { Value = "Zed (1)", State = ElementState.Edited },
            cache.Find(n["Beta"].Id));
    }

    [Fact]
    public async Task Take_database_on_a_conflicting_delete_brings_back_the_element_and_the_descendants_deleted_with_it()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"], n["A2"]);
        cache.Delete(n["Alpha"].Id);
        OnApplyConflict(api, new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "Alpha elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.TakeDatabase(n["Alpha"].Id);

        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Value = "Alpha elsewhere", Version = 2000 },
            cache.Find(n["Alpha"].Id));
        Assert.Equal(CachedElement.From(n["A1"]), cache.Find(n["A1"].Id));
        Assert.Equal(CachedElement.From(n["A2"]), cache.Find(n["A2"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Take_database_on_a_conflicting_delete_leaves_the_descendants_deleted_on_their_own_pending()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"], n["A2"]);
        cache.Delete(n["A1"].Id);
        cache.Delete(n["Alpha"].Id);
        OnApplyConflict(api, new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "Alpha elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.TakeDatabase(n["Alpha"].Id);

        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Value = "Alpha elsewhere", Version = 2000 },
            cache.Find(n["Alpha"].Id));
        Assert.Equal(ElementState.Deleted, cache.Find(n["A1"].Id)?.State);
        Assert.Equal(ElementState.Deleted, cache.Find(n["A2"].Id)?.State);
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new NodeDelete(n["A1"].Id, n["A1"].Version), Assert.Single(api.ApplyRequests[^1].Deletes));
    }

    [Fact]
    public async Task Take_database_on_a_conflicting_delete_brings_back_what_was_deleted_below_it_afterwards()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"]);
        cache.Delete(n["Alpha"].Id);
        // Loaded below A1, which was deleted with Alpha.
        await cache.LoadElementAsync(n["A2"].Id, TestContext.Current.CancellationToken);
        OnApplyConflict(api, new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "Alpha elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.TakeDatabase(n["Alpha"].Id);

        Assert.Equal(CachedElement.From(n["A1"]), cache.Find(n["A1"].Id));
        Assert.Equal(CachedElement.From(n["A2"]), cache.Find(n["A2"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Take_database_on_a_delete_that_covered_a_conflicting_delete_takes_the_database_copy_of_both()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"], n["A2"]);
        cache.Delete(n["A1"].Id);
        OnApplyConflict(api, new NodeConflict(n["A1"].Id, ConflictReason.VersionChanged, "A1 elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);
        cache.Delete(n["Alpha"].Id);
        OnApplyConflict(api, new NodeConflict(n["Alpha"].Id, ConflictReason.VersionChanged, "Alpha elsewhere", 2001, false));
        await ApplyRejectedAsync(cache);

        cache.TakeDatabase(n["Alpha"].Id);

        // Nobody chose to keep the delete of A1 over the other change: it comes back as the database has it.
        Assert.Equal(
            CachedElement.From(n["A1"]) with { Value = "A1 elsewhere", Version = 2000 },
            cache.Find(n["A1"].Id));
        Assert.Equal(CachedElement.From(n["A2"]), cache.Find(n["A2"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Keep_mine_keeps_the_edit_on_the_database_version_and_the_next_apply_sends_it()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        cache.EditValue(n["Alpha"].Id, "Alpha mine");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);
        var changes = 0;
        cache.Changed += () => changes++;

        cache.KeepMine(n["Beta"].Id);

        Assert.Equal(1, changes);
        Assert.Equal(
            CachedElement.From(n["Beta"]) with { Value = "Beta mine", Version = 2000, State = ElementState.Edited },
            cache.Find(n["Beta"].Id));
        Assert.True(cache.CanApply);
        Assert.Single(api.ApplyRequests);

        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new NodeEdit(n["Beta"].Id, "Beta mine", 2000),
                new NodeEdit(n["Alpha"].Id, "Alpha mine", n["Alpha"].Version),
            ],
            api.ApplyRequests[^1].Edits);
    }

    [Fact]
    public async Task Keep_mine_makes_the_database_copy_the_one_discard_goes_back_to()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);
        cache.KeepMine(n["Beta"].Id);

        cache.DiscardAll();

        Assert.Equal(CachedElement.From(n["Beta"]) with { Value = "Beta elsewhere", Version = 2000 }, cache.Find(n["Beta"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Keep_mine_on_a_conflicting_delete_sends_it_again_with_the_database_version()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Gamma"], n["G1"]);
        cache.Delete(n["Gamma"].Id);
        OnApplyConflict(api, new NodeConflict(n["Gamma"].Id, ConflictReason.VersionChanged, "Gamma elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.KeepMine(n["Gamma"].Id);

        Assert.Equal(
            CachedElement.From(n["Gamma"]) with
            {
                Value = "Gamma elsewhere",
                Version = 2000,
                IsDeleted = true,
                State = ElementState.Deleted,
            },
            cache.Find(n["Gamma"].Id));
        Assert.Equal(ElementState.Deleted, cache.Find(n["G1"].Id)?.State);
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(n["Gamma"].Id, "Gamma elsewhere", 5000, true)])));
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Empty(api.ApplyRequests[^1].Edits);
        Assert.Equal(new NodeDelete(n["Gamma"].Id, 2000), Assert.Single(api.ApplyRequests[^1].Deletes));
    }

    [Fact]
    public async Task Keep_mine_with_the_value_the_database_already_holds_leaves_nothing_pending()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Same");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Same", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.KeepMine(n["Beta"].Id);

        Assert.Equal(CachedElement.From(n["Beta"]) with { Value = "Same", Version = 2000 }, cache.Find(n["Beta"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Discard_all_takes_the_database_copy_of_conflicting_elements()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Alpha mine");
        cache.EditValue(n["Beta"].Id, "Beta mine");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        cache.DiscardAll();

        Assert.Equal(CachedElement.From(n["Alpha"]), cache.Find(n["Alpha"].Id));
        Assert.Equal(CachedElement.From(n["Beta"]) with { Value = "Beta elsewhere", Version = 2000 }, cache.Find(n["Beta"].Id));
        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.HasUnresolvedConflicts);
    }

    [Fact]
    public async Task Conflicting_elements_cannot_be_edited_or_deleted_until_resolved()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Beta"].Id, "Beta mine");
        OnApplyConflict(api, new NodeConflict(n["Beta"].Id, ConflictReason.VersionChanged, "Beta elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);

        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["Beta"].Id, "Other"));
        Assert.Throws<InvalidOperationException>(() => cache.Delete(n["Beta"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.TakeDatabase(n["Alpha"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.KeepMine(n["Alpha"].Id));
        Assert.Equal("Beta mine", cache.Find(n["Beta"].Id)?.Value);
    }

    [Fact]
    public async Task Deleting_an_ancestor_resolves_the_conflicts_below_it()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"], n["A2"]);
        cache.EditValue(n["A1"].Id, "A1 mine");
        cache.Delete(n["A2"].Id);
        OnApplyConflict(
            api,
            new NodeConflict(n["A1"].Id, ConflictReason.VersionChanged, "A1 elsewhere", 2000, false),
            new NodeConflict(n["A2"].Id, ConflictReason.VersionChanged, "A2 elsewhere", 2001, false));
        await ApplyRejectedAsync(cache);

        cache.Delete(n["Alpha"].Id);

        Assert.False(cache.HasUnresolvedConflicts);
        // Each conflict showed the loaded copy is stale: the delete goes on from the database's.
        Assert.Equal(
            CachedElement.From(n["A1"]) with
            {
                Value = "A1 elsewhere",
                Version = 2000,
                IsDeleted = true,
                State = ElementState.Deleted,
            },
            cache.Find(n["A1"].Id));
        Assert.Equal(
            CachedElement.From(n["A2"]) with
            {
                Value = "A2 elsewhere",
                Version = 2001,
                IsDeleted = true,
                State = ElementState.Deleted,
            },
            cache.Find(n["A2"].Id));
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Alpha", 5000, true)])));
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Empty(api.ApplyRequests[^1].Edits);
        Assert.Equal(new NodeDelete(n["Alpha"].Id, n["Alpha"].Version), Assert.Single(api.ApplyRequests[^1].Deletes));
    }

    [Fact]
    public async Task Discard_all_after_deleting_an_ancestor_takes_the_database_copy_of_the_conflicts_below_it()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["A1"], n["A2"]);
        cache.EditValue(n["A1"].Id, "A1 mine");
        cache.Delete(n["A2"].Id);
        OnApplyConflict(
            api,
            new NodeConflict(n["A1"].Id, ConflictReason.VersionChanged, "A1 elsewhere", 2000, false),
            new NodeConflict(n["A2"].Id, ConflictReason.VersionChanged, "A2 elsewhere", 2001, false));
        await ApplyRejectedAsync(cache);
        cache.Delete(n["Alpha"].Id);

        cache.DiscardAll();

        Assert.Equal(CachedElement.From(n["Alpha"]), cache.Find(n["Alpha"].Id));
        Assert.Equal(
            CachedElement.From(n["A1"]) with { Value = "A1 elsewhere", Version = 2000 },
            cache.Find(n["A1"].Id));
        Assert.Equal(
            CachedElement.From(n["A2"]) with { Value = "A2 elsewhere", Version = 2001 },
            cache.Find(n["A2"].Id));
        Assert.False(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Ancestor_loaded_deleted_resolves_the_conflicts_below_it()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["A2"]);
        cache.Delete(n["A2"].Id);
        OnApplyConflict(api, new NodeConflict(n["A2"].Id, ConflictReason.VersionChanged, "A2 elsewhere", 2000, false));
        await ApplyRejectedAsync(cache);
        tree.Delete(n["Alpha"]);

        await cache.LoadElementAsync(n["Alpha"].Id, TestContext.Current.CancellationToken);

        // Deleted in the database with Alpha: nothing to take or keep, and it can't come back live.
        Assert.False(cache.HasUnresolvedConflicts);
        Assert.False(cache.HasPendingChanges);
        Assert.Equal(CachedElement.From(n["A2"]) with { IsDeleted = true }, cache.Find(n["A2"].Id));
        Assert.Throws<InvalidOperationException>(() => cache.TakeDatabase(n["A2"].Id));
    }

    [Fact]
    public async Task Conflict_that_arrives_after_a_clear_stores_nothing()
    {
        var (tree, n) = Sample();
        var api = new FakeCacheApiClient(tree);
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Alpha mine");
        var response = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => response.Task);
        var apply = cache.ApplyAsync(cancellationToken);

        cache.Clear();
        await cache.LoadElementAsync(n["Alpha"].Id, cancellationToken);
        cache.EditValue(n["Alpha"].Id, "Alpha again");
        response.SetException(Rejection(new NodeConflict(n["Alpha"].Id, ConflictReason.Deleted, null, null, true)));
        await Assert.ThrowsAsync<ApplyRejectedException>(() => apply);

        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Value = "Alpha again", State = ElementState.Edited },
            cache.Find(n["Alpha"].Id));
        Assert.False(cache.HasUnresolvedConflicts);
        Assert.True(cache.CanApply);
    }

    /// <summary>
    /// Root with Alpha, Beta, Gamma and Delta; below Alpha a chain A1 → A2, below Gamma G1.
    /// </summary>
    private static (TestTree Tree, Dictionary<string, NodeDetails> Nodes) Sample()
    {
        var tree = new TestTree();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var chain = tree.Chain(alpha, "A", firstLevel: 1, count: 2);
        var beta = tree.Child(root, "Beta");
        var gamma = tree.Child(root, "Gamma");
        var g1 = tree.Child(gamma, "G1");
        var delta = tree.Child(root, "Delta");
        return (tree, new Dictionary<string, NodeDetails>
        {
            ["Root"] = root,
            ["Alpha"] = alpha,
            ["A1"] = chain[0],
            ["A2"] = chain[1],
            ["Beta"] = beta,
            ["Gamma"] = gamma,
            ["G1"] = g1,
            ["Delta"] = delta,
        });
    }

    private static ApplyRejectedException Rejection(params NodeConflict[] conflicts) =>
        new("The changes conflict with the database; nothing was applied.", HttpStatusCode.Conflict, conflicts);

    private static void OnApplyConflict(FakeCacheApiClient api, params NodeConflict[] conflicts) =>
        api.OnApply(_ => Task.FromException<ApplyResponse>(Rejection(conflicts)));

    private static async Task<ApplyRejectedException> ApplyRejectedAsync(LocalCache cache)
    {
        var rejected = await Assert.ThrowsAsync<ApplyRejectedException>(
            () => cache.ApplyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        return rejected;
    }
}
