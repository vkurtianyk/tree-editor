using System.Net;
using TreeEditor.Contracts;

namespace TreeEditor.Web.Api;

/// <summary>
/// The server refused an Apply and wrote nothing: invalid changes (400) or changes that conflict with the
/// database (409, with the conflicting elements in <see cref="Conflicts"/>).
/// </summary>
public sealed class ApplyRejectedException(
    string message,
    HttpStatusCode statusCode,
    IReadOnlyList<NodeConflict> conflicts) : HttpRequestException(message, inner: null, statusCode)
{
    public IReadOnlyList<NodeConflict> Conflicts { get; } = conflicts;
}
