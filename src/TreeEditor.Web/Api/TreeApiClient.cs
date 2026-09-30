using System.Net.Http.Json;
using TreeEditor.Contracts;

namespace TreeEditor.Web.Api;

/// <summary>Typed client for the tree API.</summary>
public sealed class TreeApiClient(HttpClient http)
{
    /// <summary>First page of the children of <paramref name="parentId"/>; roots when it is null.</summary>
    public async Task<ChildrenPage> ListChildrenAsync(Guid? parentId, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId), cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty children page.");
}
