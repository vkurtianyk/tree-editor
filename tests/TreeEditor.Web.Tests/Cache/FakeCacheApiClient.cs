using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Tests.Cache;

internal abstract record ApiCall;

internal sealed record LoadCall(Guid Id) : ApiCall;

internal sealed record ApplyCall(ApplyRequest Request) : ApiCall;

/// <summary>
/// The cache's API client, serving loads from a <see cref="TestTree"/> and recording every call in order.
/// Each Apply takes the next outcome a test scripted with <see cref="OnApply"/>.
/// </summary>
internal sealed class FakeCacheApiClient(TestTree tree) : ICacheApiClient
{
    private readonly List<ApiCall> calls = [];
    private readonly Queue<Func<ApplyRequest, Task<ApplyResponse>>> applyOutcomes = new();
    private readonly Queue<TaskCompletionSource> heldLoads = new();

    public IReadOnlyList<ApiCall> Calls => calls;

    public IReadOnlyList<ApplyRequest> ApplyRequests => [.. calls.OfType<ApplyCall>().Select(call => call.Request)];

    /// <summary>Scripts the next Apply: a response, a thrown error, or a task the test completes later.</summary>
    public void OnApply(Func<ApplyRequest, Task<ApplyResponse>> outcome) => applyOutcomes.Enqueue(outcome);

    /// <summary>Scripts the next Apply to succeed like a server that adds no suffix: same values, a new version.</summary>
    public void OnApplySucceed(uint newVersion) => OnApply(request => Task.FromResult(new ApplyResponse(
        [
            .. request.Inserts.Select(insert => new AppliedNode(insert.Id, insert.Value, newVersion, IsDeleted: false)),
            .. request.Edits.Select(edit => new AppliedNode(edit.Id, edit.Value, newVersion, IsDeleted: false)),
        ])));

    /// <summary>
    /// Holds the next load's response until the returned source completes. The element is read when the load
    /// starts, so it arrives as it was then, whatever changed meanwhile.
    /// </summary>
    public TaskCompletionSource HoldNextLoad()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        heldLoads.Enqueue(release);
        return release;
    }

    public async Task<NodeDetails> LoadNodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        calls.Add(new LoadCall(id));
        if (!tree.TryGet(id, out var node))
        {
            throw new HttpRequestException($"No element has the id {id}.", null, HttpStatusCode.NotFound);
        }

        if (heldLoads.TryDequeue(out var release))
        {
            await release.Task;
        }

        return node;
    }

    public Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default)
    {
        calls.Add(new ApplyCall(request));
        return applyOutcomes.TryDequeue(out var outcome)
            ? outcome(request)
            : Task.FromException<ApplyResponse>(new NotSupportedException("No Apply outcome is scripted."));
    }
}
