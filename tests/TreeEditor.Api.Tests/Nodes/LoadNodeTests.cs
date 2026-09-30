using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

namespace TreeEditor.Api.Tests.Nodes;

public sealed class LoadNodeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returns_the_element_with_its_parent_ancestors_value_and_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var middle = tree.Child(root, "Middle");
        var leaf = tree.Child(middle, "Leaf");
        tree.Child(leaf, "Below");
        await api.ArrangeAsync(tree, cancellationToken);

        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(leaf.Id), cancellationToken);

        Assert.NotNull(node);
        Assert.Equal(leaf.Id, node.Id);
        Assert.Equal(middle.Id, node.ParentId);
        Assert.Equal([root.Id, middle.Id, leaf.Id], node.Ancestors);
        Assert.Equal("Leaf", node.Value);
        Assert.NotEqual(0u, node.Version);
        Assert.Equal(leaf.Version, node.Version);
        Assert.False(node.IsDeleted);
    }

    [Fact]
    public async Task Root_has_no_parent_and_only_itself_as_ancestor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        await api.ArrangeAsync(tree, cancellationToken);

        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(root.Id), cancellationToken);

        Assert.NotNull(node);
        Assert.Null(node.ParentId);
        Assert.Equal([root.Id], node.Ancestors);
    }

    [Fact]
    public async Task Deleted_element_loads_with_its_deleted_flag()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        var root = tree.Root("Root");
        var deleted = tree.Child(root, "Gone", deleted: true);
        await api.ArrangeAsync(tree, cancellationToken);

        var node = await api.Client.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(deleted.Id), cancellationToken);

        Assert.NotNull(node);
        Assert.True(node.IsDeleted);
        Assert.Equal("Gone", node.Value);
    }

    [Fact]
    public async Task Unknown_id_is_a_404_problem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);
        var tree = new TreeBuilder();
        tree.Root("Root");
        await api.ArrangeAsync(tree, cancellationToken);
        var unknownId = Guid.CreateVersion7();

        using var response = await api.Client.GetAsync(ApiRoutes.NodeById(unknownId), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Contains(unknownId.ToString(), problem.Detail, StringComparison.Ordinal);
    }
}
