using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

namespace TreeEditor.Api.Tests.Apply;

public sealed class ApplyDeleteTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Delete_cascades_to_descendants_that_were_never_loaded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var child = tree.Child(alpha, "Child");
        var grandchild = tree.Child(child, "Grandchild");
        var beta = tree.Child(root, "Beta");
        var betaChild = tree.Child(beta, "Beta child");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(api, Deletes(new NodeDelete(alpha.Id, alpha.Version)), cancellationToken);

        var node = Assert.Single(applied.Nodes);
        Assert.Equal(alpha.Id, node.Id);
        Assert.Equal("Alpha", node.Value);
        Assert.True(node.IsDeleted);
        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.True(alphaNow.IsDeleted);
        Assert.Equal(node.Version, alphaNow.Version);

        Assert.True((await LoadAsync(api, child.Id, cancellationToken)).IsDeleted);
        Assert.True((await LoadAsync(api, grandchild.Id, cancellationToken)).IsDeleted);
        Assert.Equal([("Child", true)], await ListAsync(api, alpha.Id, cancellationToken));
        Assert.Equal([("Grandchild", true)], await ListAsync(api, child.Id, cancellationToken));

        // The rest of the tree stays live.
        Assert.Equal([("Alpha", true), ("Beta", false)], await ListAsync(api, root.Id, cancellationToken));
        Assert.False((await LoadAsync(api, root.Id, cancellationToken)).IsDeleted);
        Assert.False((await LoadAsync(api, betaChild.Id, cancellationToken)).IsDeleted);
    }

    [Fact]
    public async Task Delete_with_a_stale_version_is_a_409_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var child = tree.Child(alpha, "Child");
        await api.ArrangeAsync(tree, cancellationToken);
        // Someone else renames Alpha after this client loaded it.
        await ApplyOkAsync(api, new ApplyRequest([], [new NodeEdit(alpha.Id, "Alpha elsewhere", alpha.Version)], []), cancellationToken);
        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);

        using var response = await ApplyAsync(api, Deletes(new NodeDelete(alpha.Id, alpha.Version)), cancellationToken);

        var conflict = Assert.Single(await AssertConflictsAsync(response, cancellationToken));
        Assert.Equal(new NodeConflict(alpha.Id, ConflictReason.VersionChanged, "Alpha elsewhere", alphaNow.Version, false), conflict);
        Assert.False((await LoadAsync(api, alpha.Id, cancellationToken)).IsDeleted);
        Assert.False((await LoadAsync(api, child.Id, cancellationToken)).IsDeleted);
    }

    [Fact]
    public async Task Deletes_of_deleted_or_missing_elements_are_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var gone = tree.Child(root, "Gone", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);
        var missingId = Guid.CreateVersion7();

        using var response = await ApplyAsync(
            api,
            Deletes(new NodeDelete(gone.Id, gone.Version), new NodeDelete(missingId, 1)),
            cancellationToken);

        Assert.Equal(
            [
                new NodeConflict(gone.Id, ConflictReason.Deleted, "Gone", gone.Version, true),
                new NodeConflict(missingId, ConflictReason.Deleted, null, null, true),
            ],
            await AssertConflictsAsync(response, cancellationToken));
        Assert.Equal(gone.Version, (await LoadAsync(api, gone.Id, cancellationToken)).Version);
    }

    [Fact]
    public async Task A_conflicting_edit_keeps_the_deletes_of_the_same_request_from_being_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);
        await ApplyOkAsync(api, new ApplyRequest([], [new NodeEdit(beta.Id, "Beta elsewhere", beta.Version)], []), cancellationToken);

        using var response = await ApplyAsync(
            api,
            new ApplyRequest([], [new NodeEdit(beta.Id, "Beta mine", beta.Version)], [new NodeDelete(alpha.Id, alpha.Version)]),
            cancellationToken);

        Assert.Equal(beta.Id, Assert.Single(await AssertConflictsAsync(response, cancellationToken)).Id);
        Assert.False((await LoadAsync(api, alpha.Id, cancellationToken)).IsDeleted);
    }

    [Fact]
    public async Task A_deleted_element_frees_its_name_for_a_sibling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);
        await ApplyOkAsync(api, Deletes(new NodeDelete(alpha.Id, alpha.Version)), cancellationToken);

        var applied = await ApplyOkAsync(
            api,
            new ApplyRequest([], [new NodeEdit(beta.Id, "alpha", beta.Version)], []),
            cancellationToken);

        Assert.Equal("alpha", Assert.Single(applied.Nodes).Value);
        // Namesakes are listed in id order, which is random for ids made within one millisecond.
        var children = await ListAsync(api, root.Id, cancellationToken);
        Assert.Equal([("Alpha", true), ("alpha", false)], children.OrderBy(item => item.Value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Edits_and_deletes_in_one_request_return_every_changed_element_in_request_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        var gamma = tree.Child(root, "Gamma");
        // Deletes run after edits, so a delete also covers a descendant edited in the same request.
        var gammaChild = tree.Child(gamma, "Gamma child");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(
            api,
            new ApplyRequest(
                [],
                [new NodeEdit(alpha.Id, "Renamed", alpha.Version), new NodeEdit(gammaChild.Id, "Renamed child", gammaChild.Version)],
                [new NodeDelete(gamma.Id, gamma.Version), new NodeDelete(beta.Id, beta.Version)]),
            cancellationToken);

        Assert.Equal(
            [(alpha.Id, "Renamed", false), (gammaChild.Id, "Renamed child", true), (gamma.Id, "Gamma", true), (beta.Id, "Beta", true)],
            applied.Nodes.Select(node => (node.Id, node.Value, node.IsDeleted)));
        foreach (var node in applied.Nodes)
        {
            var stored = await LoadAsync(api, node.Id, cancellationToken);
            Assert.Equal((node.Value, node.Version, node.IsDeleted), (stored.Value, stored.Version, stored.IsDeleted));
        }
    }

    [Fact]
    public async Task Deleting_an_element_and_its_descendant_in_one_request_applies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var child = tree.Child(alpha, "Child");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(
            api,
            Deletes(new NodeDelete(alpha.Id, alpha.Version), new NodeDelete(child.Id, child.Version)),
            cancellationToken);

        Assert.All(applied.Nodes, node => Assert.True(node.IsDeleted));
        Assert.Equal([alpha.Id, child.Id], applied.Nodes.Select(node => node.Id));
        Assert.Equal(applied.Nodes[1].Version, (await LoadAsync(api, child.Id, cancellationToken)).Version);
    }

    [Fact]
    public async Task Editing_and_deleting_the_same_element_is_a_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        await api.ArrangeAsync(tree, cancellationToken);

        using var response = await ApplyAsync(
            api,
            new ApplyRequest([], [new NodeEdit(alpha.Id, "Renamed", alpha.Version)], [new NodeDelete(alpha.Id, alpha.Version), null!]),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(["deletes[0].id", "deletes[1]"], problem.Errors.Keys.Order());
        Assert.False((await LoadAsync(api, alpha.Id, cancellationToken)).IsDeleted);
    }

    private static ApplyRequest Deletes(params NodeDelete[] deletes) => new([], [], deletes);

    private static Task<HttpResponseMessage> ApplyAsync(ApiHarness api, ApplyRequest request, CancellationToken cancellationToken) =>
        api.Client.PostAsJsonAsync(ApiRoutes.Apply, request, cancellationToken);

    private static async Task<ApplyResponse> ApplyOkAsync(ApiHarness api, ApplyRequest request, CancellationToken cancellationToken)
    {
        using var response = await ApplyAsync(api, request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>(cancellationToken);
        Assert.NotNull(applied);
        return applied;
    }

    private static async Task<IReadOnlyList<NodeConflict>> AssertConflictsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ConflictProblem>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        return problem.Conflicts;
    }

    private static async Task<NodeDetails> LoadAsync(ApiHarness api, Guid id, CancellationToken cancellationToken)
    {
        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(id), cancellationToken);
        Assert.NotNull(node);
        return node;
    }

    /// <summary>The first page of an element's children as (value, deleted) pairs.</summary>
    private static async Task<List<(string Value, bool IsDeleted)>> ListAsync(
        ApiHarness api,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId), cancellationToken);
        Assert.NotNull(page);
        return [.. page.Items.Select(item => (item.Value, item.IsDeleted))];
    }

    /// <summary>Apply's 409 body: RFC 9457 ProblemDetails plus the conflicting elements.</summary>
    private sealed record ConflictProblem(int Status, string Title, IReadOnlyList<NodeConflict> Conflicts);
}
