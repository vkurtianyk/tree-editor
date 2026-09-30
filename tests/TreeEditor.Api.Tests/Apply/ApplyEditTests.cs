using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;
using TreeEditor.Domain;

namespace TreeEditor.Api.Tests.Apply;

public sealed class ApplyEditTests(PostgresFixture postgres)
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task Edit_stores_the_trimmed_value_and_returns_the_final_value_and_new_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        await api.ArrangeAsync(tree, cancellationToken);

        using var response = await ApplyAsync(api, Edits(new NodeEdit(alpha.Id, "  Renamed  ", alpha.Version)), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>(cancellationToken);
        Assert.NotNull(applied);
        var node = Assert.Single(applied.Nodes);
        Assert.Equal(alpha.Id, node.Id);
        Assert.Equal("Renamed", node.Value);
        Assert.NotEqual(alpha.Version, node.Version);
        Assert.False(node.IsDeleted);

        var stored = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.Equal("Renamed", stored.Value);
        Assert.Equal(node.Version, stored.Version);
    }

    [Fact]
    public async Task Rename_colliding_with_a_never_loaded_sibling_gets_the_first_free_suffix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        tree.Child(root, "Beta");
        tree.Child(root, "BETA (2)");
        // Deleted siblings don't hold a value.
        tree.Child(root, "Beta (1)", deleted: true);
        // Nor do elements elsewhere in the tree.
        tree.Child(alpha, "beta (1)");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(api, Edits(new NodeEdit(alpha.Id, "beta", alpha.Version)), cancellationToken);

        Assert.Equal("beta (1)", Assert.Single(applied.Nodes).Value);
        Assert.Equal("beta (1)", (await LoadAsync(api, alpha.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Rename_does_not_collide_with_the_element_itself()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(api, Edits(new NodeEdit(alpha.Id, "ALPHA", alpha.Version)), cancellationToken);

        Assert.Equal("ALPHA", Assert.Single(applied.Nodes).Value);
    }

    [Fact]
    public async Task Root_rename_colliding_with_another_root_gets_a_suffix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        tree.Root("Alpha");
        var beta = tree.Root("Beta");
        // A child with the same value is not a sibling of a root.
        tree.Child(beta, "alpha (1)");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(api, Edits(new NodeEdit(beta.Id, "alpha", beta.Version)), cancellationToken);

        Assert.Equal("alpha (1)", Assert.Single(applied.Nodes).Value);
        Assert.Equal("alpha (1)", (await LoadAsync(api, beta.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Edits_are_resolved_in_request_order_so_later_ones_see_earlier_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(
            api,
            Edits(new NodeEdit(alpha.Id, "Gamma", alpha.Version), new NodeEdit(beta.Id, "Gamma", beta.Version)),
            cancellationToken);

        Assert.Equal(["Gamma", "Gamma (1)"], applied.Nodes.Select(node => node.Value));
        Assert.Equal([alpha.Id, beta.Id], applied.Nodes.Select(node => node.Id));
    }

    [Fact]
    public async Task Swapping_two_sibling_values_suffixes_the_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);

        var applied = await ApplyOkAsync(
            api,
            Edits(new NodeEdit(alpha.Id, "Beta", alpha.Version), new NodeEdit(beta.Id, "Alpha", beta.Version)),
            cancellationToken);

        Assert.Equal(["Beta (1)", "Alpha"], applied.Nodes.Select(node => node.Value));
    }

    [Fact]
    public async Task Stale_version_is_a_409_listing_the_element_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);
        // Someone else renames Beta after this client loaded it.
        await ApplyOkAsync(api, Edits(new NodeEdit(beta.Id, "Beta elsewhere", beta.Version)), cancellationToken);
        var betaNow = await LoadAsync(api, beta.Id, cancellationToken);

        using var response = await ApplyAsync(
            api,
            Edits(new NodeEdit(alpha.Id, "Alpha mine", alpha.Version), new NodeEdit(beta.Id, "Beta mine", beta.Version)),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ConflictProblem>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        var conflict = Assert.Single(problem.Conflicts);
        Assert.Equal(new NodeConflict(beta.Id, ConflictReason.VersionChanged, "Beta elsewhere", betaNow.Version, false), conflict);

        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.Equal("Alpha", alphaNow.Value);
        Assert.Equal(alpha.Version, alphaNow.Version);
        Assert.Equal("Beta elsewhere", (await LoadAsync(api, beta.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Edits_of_deleted_or_missing_elements_are_conflicts()
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
            Edits(new NodeEdit(gone.Id, "Back", gone.Version), new NodeEdit(missingId, "Never", 1)),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ConflictProblem>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(
            [
                new NodeConflict(gone.Id, ConflictReason.Deleted, "Gone", gone.Version, true),
                new NodeConflict(missingId, ConflictReason.Deleted, null, null, true),
            ],
            problem.Conflicts);
        Assert.Equal("Gone", (await LoadAsync(api, gone.Id, cancellationToken)).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Missing_value_is_a_400_and_nothing_is_written(string? value)
    {
        await AssertInvalidValueIsRejectedAsync(value);
    }

    [Fact]
    public async Task Too_long_value_is_a_400_and_nothing_is_written()
    {
        await AssertInvalidValueIsRejectedAsync(new string('x', ElementValue.MaxLength + 1));
    }

    [Fact]
    public async Task Duplicate_ids_are_a_400_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        await api.ArrangeAsync(tree, cancellationToken);

        using var response = await ApplyAsync(
            api,
            Edits(new NodeEdit(alpha.Id, "One", alpha.Version), new NodeEdit(alpha.Id, "Two", alpha.Version)),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["edits[1].id"], problem.Errors.Keys);
        Assert.Equal("Alpha", (await LoadAsync(api, alpha.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Inserts_are_not_supported_yet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        await api.ArrangeAsync(tree, cancellationToken);

        using var response = await ApplyAsync(
            api,
            new ApplyRequest([new NodeInsert(Guid.CreateVersion7(), alpha.Id, "New")], [], [new NodeDelete(alpha.Id, alpha.Version)]),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["inserts"], problem.Errors.Keys);
        Assert.False((await LoadAsync(api, alpha.Id, cancellationToken)).IsDeleted);
    }

    [Fact]
    public async Task Empty_request_applies_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);

        var applied = await ApplyOkAsync(api, Edits(), cancellationToken);

        Assert.Empty(applied.Nodes);
    }

    private async Task AssertInvalidValueIsRejectedAsync(string? value)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        await api.ArrangeAsync(tree, cancellationToken);

        using var response = await ApplyAsync(
            api,
            Edits(new NodeEdit(alpha.Id, "Alpha mine", alpha.Version), new NodeEdit(beta.Id, value!, beta.Version)),
            cancellationToken);

        var problem = await AssertValidationProblemAsync(response, cancellationToken);
        Assert.Equal(["edits[1].value"], problem.Errors.Keys);
        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.Equal("Alpha", alphaNow.Value);
        Assert.Equal(alpha.Version, alphaNow.Version);
    }

    private static ApplyRequest Edits(params NodeEdit[] edits) => new([], edits, []);

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
