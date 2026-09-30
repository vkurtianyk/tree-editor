using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

namespace TreeEditor.Api.Tests.Apply;

public sealed class ApplyInsertTests(PostgresFixture postgres)
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task Insert_chain_in_one_request_is_written_parent_before_child()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var middle = tree.Child(root, "Middle");
        await api.ArrangeAsync(tree, cancellationToken);
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        // Children come before their parents in the request; the server still writes parents first.
        var applied = await ApplyOkAsync(
            api,
            Inserts(new NodeInsert(c, b, "  C  "), new NodeInsert(b, a, "B"), new NodeInsert(a, middle.Id, "A")),
            cancellationToken);

        Assert.Equal([a, b, c], applied.Nodes.Select(node => node.Id));
        Assert.Equal(["A", "B", "C"], applied.Nodes.Select(node => node.Value));
        Assert.All(applied.Nodes, node => Assert.False(node.IsDeleted));
        foreach (var node in applied.Nodes)
        {
            var stored = await LoadAsync(api, node.Id, cancellationToken);
            Assert.Equal(node.Value, stored.Value);
            Assert.Equal(node.Version, stored.Version);
            Assert.NotEqual(0u, stored.Version);
            Assert.False(stored.IsDeleted);
        }

        Assert.Equal([root.Id, middle.Id, a, b, c], (await LoadAsync(api, c, cancellationToken)).Ancestors);
        Assert.Equal(b, (await LoadAsync(api, c, cancellationToken)).ParentId);
    }

    [Fact]
    public async Task Ancestors_are_computed_by_the_server_from_the_parent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var middle = tree.Child(root, "Middle");
        var other = tree.Root("Other");
        await api.ArrangeAsync(tree, cancellationToken);
        var (child, grandchild) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        // Ancestor lists sent along are not part of the contract and are ignored.
        using var response = await api.Client.PostAsJsonAsync(
            ApiRoutes.Apply,
            new
            {
                inserts = new object[]
                {
                    new { id = child, parentId = middle.Id, value = "Child", ancestors = new[] { other.Id, child } },
                    new { id = grandchild, parentId = child, value = "Grandchild", ancestors = new[] { grandchild } },
                },
                edits = Array.Empty<object>(),
                deletes = Array.Empty<object>(),
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadAsync(api, grandchild, cancellationToken);
        Assert.Equal(child, stored.ParentId);
        Assert.Equal([root.Id, middle.Id, child, grandchild], stored.Ancestors);
        Assert.Equal([root.Id, middle.Id, child], (await LoadAsync(api, child, cancellationToken)).Ancestors);
    }

    [Fact]
    public async Task Insert_colliding_with_a_never_loaded_sibling_gets_the_first_free_suffix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        tree.Child(root, "Beta");
        tree.Child(root, "BETA (2)");
        // Deleted siblings don't hold a value.
        tree.Child(root, "Beta (1)", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);
        var id = Guid.CreateVersion7();

        var applied = await ApplyOkAsync(api, Inserts(new NodeInsert(id, root.Id, "beta")), cancellationToken);

        Assert.Equal("beta (1)", Assert.Single(applied.Nodes).Value);
        Assert.Equal("beta (1)", (await LoadAsync(api, id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Two_same_named_inserts_under_one_parent_get_distinct_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var other = tree.Child(root, "Other");
        await api.ArrangeAsync(tree, cancellationToken);
        var (first, second, elsewhere) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        var applied = await ApplyOkAsync(
            api,
            Inserts(
                new NodeInsert(first, root.Id, "New"),
                new NodeInsert(second, root.Id, "new"),
                // Same value under another parent: not a sibling.
                new NodeInsert(elsewhere, other.Id, "New")),
            cancellationToken);

        Assert.Equal(["New", "new (1)", "New"], applied.Nodes.Select(node => node.Value));
        Assert.Equal("new (1)", (await LoadAsync(api, second, cancellationToken)).Value);
    }

    [Fact]
    public async Task Inserts_are_written_before_edits_so_an_edit_sees_new_siblings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        await api.ArrangeAsync(tree, cancellationToken);
        var id = Guid.CreateVersion7();

        var applied = await ApplyOkAsync(
            api,
            new ApplyRequest([new NodeInsert(id, root.Id, "Gamma")], [new NodeEdit(alpha.Id, "Gamma", alpha.Version)], []),
            cancellationToken);

        Assert.Equal([(id, "Gamma"), (alpha.Id, "Gamma (1)")], applied.Nodes.Select(node => (node.Id, node.Value)));
    }

    [Fact]
    public async Task Insert_under_a_parent_whose_value_changed_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var parent = tree.Child(root, "Parent");
        await api.ArrangeAsync(tree, cancellationToken);
        // Someone else renames the parent after this client loaded it.
        await ApplyOkAsync(api, new ApplyRequest([], [new NodeEdit(parent.Id, "Renamed", parent.Version)], []), cancellationToken);
        var id = Guid.CreateVersion7();

        var applied = await ApplyOkAsync(api, Inserts(new NodeInsert(id, parent.Id, "Child")), cancellationToken);

        Assert.Equal(id, Assert.Single(applied.Nodes).Id);
        Assert.Equal(parent.Id, (await LoadAsync(api, id, cancellationToken)).ParentId);
    }

    [Fact]
    public async Task Insert_under_a_deleted_parent_is_a_409_listing_the_parent_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var gone = tree.Child(root, "Gone", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);
        var (live, underGone, below) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        using var response = await ApplyAsync(
            api,
            Inserts(
                new NodeInsert(live, root.Id, "Live"),
                new NodeInsert(underGone, gone.Id, "Under gone"),
                new NodeInsert(below, underGone, "Below")),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ConflictProblem>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal([new NodeConflict(gone.Id, ConflictReason.Deleted, "Gone", gone.Version, true)], problem.Conflicts);
        foreach (var id in new[] { live, underGone, below })
        {
            await AssertNotFoundAsync(api, id, cancellationToken);
        }
    }

    [Fact]
    public async Task Insert_under_a_missing_parent_is_a_409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        tree.Root("Root");
        await api.ArrangeAsync(tree, cancellationToken);
        var (missingParent, first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        using var response = await ApplyAsync(
            api,
            Inserts(new NodeInsert(first, missingParent, "First"), new NodeInsert(second, missingParent, "Second")),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ConflictProblem>(cancellationToken);
        Assert.NotNull(problem);
        // Listed once, like an element that no longer exists.
        Assert.Equal([new NodeConflict(missingParent, ConflictReason.Deleted, null, null, true)], problem.Conflicts);
        await AssertNotFoundAsync(api, first, cancellationToken);
    }

    [Fact]
    public async Task Insert_with_an_existing_id_is_a_400_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        // Existing ids are rejected even when deleted.
        var gone = tree.Child(root, "Gone", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);
        var fresh = Guid.CreateVersion7();

        using var response = await ApplyAsync(
            api,
            Inserts(
                new NodeInsert(fresh, root.Id, "Fresh"),
                new NodeInsert(alpha.Id, root.Id, "Again"),
                new NodeInsert(gone.Id, root.Id, "Back")),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["inserts[1].id", "inserts[2].id"], problem.Errors.Keys.Order());
        await AssertNotFoundAsync(api, fresh, cancellationToken);
        Assert.Equal("Alpha", (await LoadAsync(api, alpha.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Invalid_insert_values_are_a_400_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        await api.ArrangeAsync(tree, cancellationToken);
        var fresh = Guid.CreateVersion7();

        using var response = await ApplyAsync(
            api,
            Inserts(
                new NodeInsert(fresh, root.Id, "Fresh"),
                new NodeInsert(Guid.CreateVersion7(), root.Id, "   "),
                new NodeInsert(Guid.CreateVersion7(), root.Id, new string('x', 256))),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["inserts[1].value", "inserts[2].value"], problem.Errors.Keys.Order());
        await AssertNotFoundAsync(api, fresh, cancellationToken);
    }

    [Fact]
    public async Task Malformed_insert_ids_are_a_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        await api.ArrangeAsync(tree, cancellationToken);
        var (twice, own, loopA, loopB) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        using var response = await ApplyAsync(
            api,
            new ApplyRequest(
                [
                    new NodeInsert(twice, root.Id, "Once"),
                    new NodeInsert(twice, root.Id, "Twice"),
                    new NodeInsert(Guid.Empty, root.Id, "Empty"),
                ],
                // An element is inserted or edited, not both.
                [new NodeEdit(twice, "Edited", 1)],
                []),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["edits[0].id", "inserts[1].id", "inserts[2].id"], problem.Errors.Keys.Order());
        await AssertNotFoundAsync(api, twice, cancellationToken);

        // Parents that are the request's own inserts must lead back to an existing element.
        using var cycle = await ApplyAsync(
            api,
            Inserts(
                new NodeInsert(own, own, "Own parent"),
                new NodeInsert(loopA, loopB, "A"),
                new NodeInsert(loopB, loopA, "B")),
            cancellationToken);

        var cycleProblem = await AssertValidationProblemAsync(cycle, cancellationToken);
        Assert.Contains("inserts[0].parentId", cycleProblem.Errors.Keys);
        Assert.Contains(cycleProblem.Errors.Keys, key => key is "inserts[1].parentId" or "inserts[2].parentId");
    }

    private static ApplyRequest Inserts(params NodeInsert[] inserts) => new(inserts, [], []);

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

    private static async Task<HttpValidationProblemDetails> AssertValidationProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        return problem;
    }

    /// <summary>Apply's 409 body: RFC 9457 ProblemDetails plus the conflicting elements.</summary>
    private sealed record ConflictProblem(int Status, string Title, IReadOnlyList<NodeConflict> Conflicts);
}
