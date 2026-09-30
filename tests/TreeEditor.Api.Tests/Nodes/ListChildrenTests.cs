using System.Net.Http.Json;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

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

        var page = await api.Client.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(null), cancellationToken);

        NodeListItem[] expected =
        [
            new(alpha.Id, "Alpha", IsDeleted: false, HasChildren: true),
            new(beta.Id, "beta", IsDeleted: false, HasChildren: false),
            new(gamma.Id, "Gamma", IsDeleted: true, HasChildren: false),
        ];
        Assert.NotNull(page);
        Assert.Equal(expected, page.Items);
        Assert.False(page.HasMore);
    }
}
