using System.Net.Http.Json;
using TreeEditor.Contracts;

namespace TreeEditor.Web.Api;

/// <summary>Typed client for the tree API.</summary>
public sealed class TreeApiClient(HttpClient http)
{
    /// <summary>
    /// A page of the children of <paramref name="parentId"/> (roots when it is null): the first page,
    /// or the one after <paramref name="after"/> (a previous page's <see cref="ChildrenPage.Next"/>).
    /// </summary>
    public async Task<ChildrenPage> ListChildrenAsync(
        Guid? parentId,
        ChildrenCursor? after = null,
        CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(parentId, after), cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty children page.");
}
