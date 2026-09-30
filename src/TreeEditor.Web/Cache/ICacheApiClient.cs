using TreeEditor.Contracts;

namespace TreeEditor.Web.Cache;

/// <summary>The only way the <see cref="LocalCache"/> reaches the server: load node and apply.</summary>
public interface ICacheApiClient
{
    /// <summary>One element by id. Fails with an <see cref="HttpRequestException"/> when no element has the id.</summary>
    Task<NodeDetails> LoadNodeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends every pending change in one all-or-nothing request. Fails with an
    /// <see cref="Api.ApplyRejectedException"/> when the server refuses the changes (400) or finds them in conflict
    /// with the database (409, listing the conflicting elements).
    /// </summary>
    Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default);
}
