using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Web.Cache;
using static TreeEditor.Web.Tests.Cache.LocalCacheEditTests;

namespace TreeEditor.Web.Tests.Cache;

public sealed class LocalCacheApplyTests
{
    [Fact]
    public async Task Apply_sends_exactly_the_pending_edits_in_edit_order_with_their_loaded_versions()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"], n["Delta"]);
        cache.EditValue(n["Beta"].Id, "Epsilon");
        cache.EditValue(n["Alpha"].Id, "Gamma");
        cache.EditValue(n["Beta"].Id, "Zeta");
        api.OnApplySucceed(newVersion: 5000);

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(api.ApplyRequests);
        Assert.Empty(request.Inserts);
        Assert.Empty(request.Deletes);
        Assert.Equal(
            [
                new NodeEdit(n["Beta"].Id, "Zeta", n["Beta"].Version),
                new NodeEdit(n["Alpha"].Id, "Gamma", n["Alpha"].Version),
            ],
            request.Edits);
    }

    [Fact]
    public async Task Success_stores_final_values_and_versions_and_clears_pending_state()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Delta");
        // The server resolved a suffix against Delta, which isn't cached.
        api.OnApply(_ => Task.FromResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Delta (1)", 5000, IsDeleted: false)])));
        var applied = 0;
        cache.Applied += () => applied++;

        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(cache.HasPendingChanges);
        Assert.False(cache.IsApplying);
        Assert.Equal(
            CachedElement.From(n["Alpha"]) with { Value = "Delta (1)", Version = 5000 },
            cache.Find(n["Alpha"].Id));
        Assert.Equal(["… 1 (Root)", "  Beta", "  Delta (1)"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Applied_values_become_the_new_baseline()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        cache.EditValue(n["Alpha"].Id, "Omega");
        cache.DiscardAll();

        Assert.Equal("Gamma", cache.Find(n["Alpha"].Id)?.Value);
        cache.EditValue(n["Alpha"].Id, "Omega");
        api.OnApplySucceed(newVersion: 5001);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new NodeEdit(n["Alpha"].Id, "Omega", 5000), Assert.Single(api.ApplyRequests[^1].Edits));
    }

    [Fact]
    public async Task While_applying_the_state_is_exposed_and_apply_is_disabled()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        var response = new TaskCompletionSource<ApplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnApply(_ => response.Task);
        Assert.True(cache.CanApply);
        var states = new List<bool>();
        cache.Changed += () => states.Add(cache.IsApplying);

        var apply = cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.True(cache.IsApplying);
        Assert.False(cache.CanApply);
        Assert.True(cache.HasPendingChanges);
        // A second Apply while one is in flight sends nothing.
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Single(api.ApplyRequests);
        // Edits and discard wait for the response.
        Assert.Throws<InvalidOperationException>(() => cache.EditValue(n["Alpha"].Id, "Omega"));
        Assert.Throws<InvalidOperationException>(cache.DiscardAll);

        response.SetResult(new ApplyResponse([new AppliedNode(n["Alpha"].Id, "Gamma", 5000, IsDeleted: false)]));
        await apply;

        Assert.False(cache.IsApplying);
        Assert.Equal([true, false], states);
        Assert.Single(api.ApplyRequests);
    }

    [Fact]
    public async Task Failure_keeps_everything_pending_and_surfaces_the_error()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"], n["Beta"]);
        cache.EditValue(n["Alpha"].Id, "Gamma");
        var error = new HttpRequestException("The server failed.", null, HttpStatusCode.InternalServerError);
        api.OnApply(_ => Task.FromException<ApplyResponse>(error));
        var applied = 0;
        cache.Applied += () => applied++;

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => cache.ApplyAsync(TestContext.Current.CancellationToken));

        Assert.Same(error, thrown);
        Assert.False(cache.IsApplying);
        Assert.True(cache.CanApply);
        Assert.Equal(ElementState.Edited, cache.Find(n["Alpha"].Id)?.State);
        Assert.Equal(["… 1 (Root)", "  Beta", "  Gamma [edited]"], Outline.Of(cache.ViewTree, tree));
        Assert.Equal(0, applied);

        // The same edit goes again.
        api.OnApplySucceed(newVersion: 5000);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(api.ApplyRequests[0].Edits, api.ApplyRequests[1].Edits);
    }

    [Fact]
    public async Task Apply_with_nothing_pending_sends_nothing()
    {
        var (tree, n) = Siblings();
        var api = new FakeCacheApiClient(tree);
        var cache = await LoadedAsync(api, n["Alpha"]);

        Assert.False(cache.CanApply);
        await cache.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Empty(api.ApplyRequests);
    }
}
