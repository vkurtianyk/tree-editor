using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Tests.Cache;

internal abstract record ApiCall;

internal sealed record LoadCall(Guid Id) : ApiCall;

internal sealed record ApplyCall(ApplyRequest Request) : ApiCall;

/// <summary>The cache's API client, serving loads from a <see cref="TestTree"/> and recording every call in order.</summary>
internal sealed class FakeCacheApiClient(TestTree tree) : ICacheApiClient
{
    private readonly List<ApiCall> calls = [];

    public IReadOnlyList<ApiCall> Calls => calls;

    public Task<NodeDetails> LoadNodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        calls.Add(new LoadCall(id));
        return tree.TryGet(id, out var node)
            ? Task.FromResult(node)
            : Task.FromException<NodeDetails>(
                new HttpRequestException($"No element has the id {id}.", null, HttpStatusCode.NotFound));
    }

    public Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default)
    {
        calls.Add(new ApplyCall(request));
        return Task.FromException<ApplyResponse>(new NotSupportedException("No Apply outcome is scripted."));
    }
}
