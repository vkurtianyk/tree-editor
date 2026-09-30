using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Posts the changes. A 400 or 409 problem throws an <see cref="ApplyRejectedException"/> carrying the problem's
    /// title, details and conflicts; any other failure throws an <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(ApiRoutes.Apply, request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw await RejectionAsync(response, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApplyResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty Apply response.");
    }

    private static async Task<ApplyRejectedException> RejectionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = response.Content.Headers.ContentType?.MediaType is "application/problem+json"
            ? await response.Content.ReadFromJsonAsync<ApplyProblemBody>(cancellationToken)
            : null;
        var explanation = problem?.Errors is { Count: > 0 } errors
            ? string.Join(" ", errors.Values.SelectMany(messages => messages))
            : problem?.Detail;
        var message = string.Join(
            " ",
            new[] { problem?.Title ?? $"The server refused the changes ({(int)response.StatusCode}).", explanation }
                .Where(part => !string.IsNullOrEmpty(part)));
        return new ApplyRejectedException(message, response.StatusCode, problem?.Conflicts ?? []);
    }

    /// <summary>The members of a 400 or 409 Apply problem the client reads.</summary>
    private sealed record ApplyProblemBody(
        string? Title,
        string? Detail,
        Dictionary<string, string[]>? Errors,
        [property: JsonPropertyName(ApplyProblem.ConflictsMember)] List<NodeConflict>? Conflicts);
}
