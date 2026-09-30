using System.Net.Http.Json;
using TreeEditor.Contracts;
using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Api;

/// <summary>Typed client for the tree API.</summary>
public sealed class TreeApiClient(HttpClient http) : ICacheApiClient
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

    public async Task<NodeDetails> LoadNodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<NodeDetails>(ApiRoutes.NodeById(id), cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty element.");

    public Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("The Apply endpoint does not exist yet.");

    /// <summary>Restores the sample data in the database. Throws <see cref="HttpRequestException"/> when it failed.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(ApiRoutes.Reset, content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
