using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

namespace TreeEditor.Api.Tests.Apply;

/// <summary>Apply requests that meet changes made elsewhere: every conflict is listed and nothing is written.</summary>
public sealed class ApplyConflictTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_conflict_across_inserts_edits_and_deletes_is_listed_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var beta = tree.Child(root, "Beta");
        var gamma = tree.Child(root, "Gamma");
        var delta = tree.Child(root, "Delta");
        var goneEdited = tree.Child(root, "Gone edited", deleted: true);
        var goneDeleted = tree.Child(root, "Gone deleted", deleted: true);
        var goneParent = tree.Child(root, "Gone parent", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);
        // Someone else renames Beta and Delta after this client loaded them.
        await ApplyOkAsync(
            api,
            new ApplyRequest(
                [],
                [new NodeEdit(beta.Id, "Beta elsewhere", beta.Version), new NodeEdit(delta.Id, "Delta elsewhere", delta.Version)],
                []),
            cancellationToken);
        var betaNow = await LoadAsync(api, beta.Id, cancellationToken);
        var deltaNow = await LoadAsync(api, delta.Id, cancellationToken);
        var (validInsert, underGone, underMissing) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (missingParent, missingEdited, missingDeleted) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        using var response = await ApplyAsync(
            api,
            new ApplyRequest(
                [
                    new NodeInsert(validInsert, root.Id, "Fine"),
                    new NodeInsert(underGone, goneParent.Id, "Orphan"),
                    new NodeInsert(underMissing, missingParent, "Orphan too"),
                ],
                [
                    new NodeEdit(alpha.Id, "Alpha mine", alpha.Version),
                    new NodeEdit(beta.Id, "Beta mine", beta.Version),
                    new NodeEdit(goneEdited.Id, "Back", goneEdited.Version),
                    new NodeEdit(missingEdited, "Never", 1),
                ],
                [
                    new NodeDelete(gamma.Id, gamma.Version),
                    new NodeDelete(delta.Id, delta.Version),
                    new NodeDelete(goneDeleted.Id, goneDeleted.Version),
                    new NodeDelete(missingDeleted, 1),
                ]),
            cancellationToken);

        // Insert parents first, then edits, then deletes, each in request order.
        Assert.Equal(
            [
                new NodeConflict(goneParent.Id, ConflictReason.Deleted, "Gone parent", goneParent.Version, true),
                new NodeConflict(missingParent, ConflictReason.Deleted, null, null, true),
                new NodeConflict(beta.Id, ConflictReason.VersionChanged, "Beta elsewhere", betaNow.Version, false),
                new NodeConflict(goneEdited.Id, ConflictReason.Deleted, "Gone edited", goneEdited.Version, true),
                new NodeConflict(missingEdited, ConflictReason.Deleted, null, null, true),
                new NodeConflict(delta.Id, ConflictReason.VersionChanged, "Delta elsewhere", deltaNow.Version, false),
                new NodeConflict(goneDeleted.Id, ConflictReason.Deleted, "Gone deleted", goneDeleted.Version, true),
                new NodeConflict(missingDeleted, ConflictReason.Deleted, null, null, true),
            ],
            await AssertConflictsAsync(response, cancellationToken));

        // The valid changes of the request were rolled back with the conflicting ones.
        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        Assert.Equal(("Alpha", alpha.Version), (alphaNow.Value, alphaNow.Version));
        var gammaNow = await LoadAsync(api, gamma.Id, cancellationToken);
        Assert.Equal((false, gamma.Version), (gammaNow.IsDeleted, gammaNow.Version));
        Assert.Equal(betaNow.Version, (await LoadAsync(api, beta.Id, cancellationToken)).Version);
        Assert.Equal(deltaNow.Version, (await LoadAsync(api, delta.Id, cancellationToken)).Version);
        foreach (var id in new[] { validInsert, underGone, underMissing })
        {
            using var load = await api.Client.GetAsync(ApiRoutes.NodeById(id), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, load.StatusCode);
        }
    }

    [Fact]
    public async Task An_element_deleted_elsewhere_with_its_ancestor_conflicts_with_its_current_value_and_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var alpha = tree.Child(root, "Alpha");
        var child = tree.Child(alpha, "Child");
        var grandchild = tree.Child(child, "Grandchild");
        await api.ArrangeAsync(tree, cancellationToken);
        // Another tab deletes Alpha; the cascade reaches Child and Grandchild, which this client loaded live.
        await ApplyOkAsync(api, new ApplyRequest([], [], [new NodeDelete(alpha.Id, alpha.Version)]), cancellationToken);
        var childNow = await LoadAsync(api, child.Id, cancellationToken);
        var grandchildNow = await LoadAsync(api, grandchild.Id, cancellationToken);

        using var response = await ApplyAsync(
            api,
            new ApplyRequest(
                [new NodeInsert(Guid.CreateVersion7(), child.Id, "New")],
                [new NodeEdit(child.Id, "Child mine", child.Version)],
                [new NodeDelete(grandchild.Id, grandchild.Version)]),
            cancellationToken);

        // Child is both the insert's parent and edited: one conflict for it.
        Assert.Equal(
            [
                new NodeConflict(child.Id, ConflictReason.Deleted, "Child", childNow.Version, true),
                new NodeConflict(grandchild.Id, ConflictReason.Deleted, "Grandchild", grandchildNow.Version, true),
            ],
            await AssertConflictsAsync(response, cancellationToken));
        Assert.Equal("Child", (await LoadAsync(api, child.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task After_a_reset_seed_elements_changed_and_elements_created_since_are_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var seed = new TreeBuilder();
        var home = seed.Root("Home");
        var kitchen = seed.Child(home, "Kitchen");
        await api.ArrangeSeedAsync(seed, cancellationToken);
        var added = new TreeBuilder();
        var spoon = added.Child(kitchen, "Spoon");
        var fork = added.Child(kitchen, "Fork");
        await api.ArrangeAsync(added, cancellationToken);
        var kitchenLoaded = await LoadAsync(api, kitchen.Id, cancellationToken);
        // Another tab resets the data: the seed comes back as fresh rows, Spoon and Fork are gone entirely.
        using (var reset = await api.Client.PostAsync(ApiRoutes.Reset, content: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        var kitchenNow = await LoadAsync(api, kitchen.Id, cancellationToken);

        using var response = await ApplyAsync(
            api,
            new ApplyRequest(
                [new NodeInsert(Guid.CreateVersion7(), fork.Id, "Tine")],
                [new NodeEdit(kitchen.Id, "Kitchen mine", kitchenLoaded.Version), new NodeEdit(spoon.Id, "Spoon mine", spoon.Version)],
                []),
            cancellationToken);

        Assert.Equal(
            [
                new NodeConflict(fork.Id, ConflictReason.Deleted, null, null, true),
                new NodeConflict(kitchen.Id, ConflictReason.VersionChanged, "Kitchen", kitchenNow.Version, false),
                new NodeConflict(spoon.Id, ConflictReason.Deleted, null, null, true),
            ],
            await AssertConflictsAsync(response, cancellationToken));
        Assert.Equal("Kitchen", (await LoadAsync(api, kitchen.Id, cancellationToken)).Value);
    }

    [Fact]
    public async Task Conflict_problem_wire_shape_is_camel_case_with_the_reason_as_a_string()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var alpha = tree.Root("Alpha");
        await api.ArrangeAsync(tree, cancellationToken);
        await ApplyOkAsync(api, new ApplyRequest([], [new NodeEdit(alpha.Id, "Alpha elsewhere", alpha.Version)], []), cancellationToken);
        var alphaNow = await LoadAsync(api, alpha.Id, cancellationToken);
        var missingId = Guid.CreateVersion7();

        using var response = await ApplyAsync(
            api,
            new ApplyRequest([], [new NodeEdit(alpha.Id, "Mine", alpha.Version)], [new NodeDelete(missingId, 1)]),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var problem = body.RootElement;
        Assert.Equal(StatusCodes.Status409Conflict, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.Equal("2 element(s) changed in the database after they were loaded.", problem.GetProperty("detail").GetString());
        var conflicts = problem.GetProperty(ApplyProblem.ConflictsMember).EnumerateArray().ToArray();
        Assert.Equal(2, conflicts.Length);

        Assert.Equal(alpha.Id, conflicts[0].GetProperty("id").GetGuid());
        Assert.Equal("VersionChanged", conflicts[0].GetProperty("reason").GetString());
        Assert.Equal("Alpha elsewhere", conflicts[0].GetProperty("value").GetString());
        Assert.Equal(alphaNow.Version, conflicts[0].GetProperty("version").GetUInt32());
        Assert.False(conflicts[0].GetProperty("isDeleted").GetBoolean());

        Assert.Equal(missingId, conflicts[1].GetProperty("id").GetGuid());
        Assert.Equal("Deleted", conflicts[1].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, conflicts[1].GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, conflicts[1].GetProperty("version").ValueKind);
        Assert.True(conflicts[1].GetProperty("isDeleted").GetBoolean());
    }

    private static Task<HttpResponseMessage> ApplyAsync(ApiHarness api, ApplyRequest request, CancellationToken cancellationToken) =>
        api.Client.PostAsJsonAsync(ApiRoutes.Apply, request, cancellationToken);

    private static async Task ApplyOkAsync(ApiHarness api, ApplyRequest request, CancellationToken cancellationToken)
    {
        using var response = await ApplyAsync(api, request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    /// <summary>Apply's 409 body: RFC 9457 ProblemDetails plus the conflicting elements.</summary>
    private sealed record ConflictProblem(int Status, string Title, IReadOnlyList<NodeConflict> Conflicts);
}
