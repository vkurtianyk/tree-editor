using System.Text.Json.Serialization;

namespace TreeEditor.Contracts;

/// <summary>
/// Every pending change of a local cache, applied in one all-or-nothing transaction. The parent of an insert
/// may be another insert of the same request.
/// </summary>
public sealed record ApplyRequest(
    IReadOnlyList<NodeInsert> Inserts,
    IReadOnlyList<NodeEdit> Edits,
    IReadOnlyList<NodeDelete> Deletes);

/// <summary>A new element under <see cref="ParentId"/>, with its id chosen by the client.</summary>
public sealed record NodeInsert(Guid Id, Guid ParentId, string Value);

/// <summary>A new value for an element, checked against the version the client loaded.</summary>
public sealed record NodeEdit(Guid Id, string Value, uint Version);

/// <summary>A delete of an element and its whole subtree, checked against the version the client loaded.</summary>
public sealed record NodeDelete(Guid Id, uint Version);

/// <summary>
/// The outcome of a successful Apply: every inserted, edited or deleted element with its final value, new version
/// and deleted flag. Cascaded descendants aren't listed.
/// </summary>
public sealed record ApplyResponse(IReadOnlyList<AppliedNode> Nodes);

public sealed record AppliedNode(Guid Id, string Value, uint Version, bool IsDeleted);

/// <summary>Why a change can't be applied.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictReason>))]
public enum ConflictReason
{
    /// <summary>The element changed in the database since the client loaded it.</summary>
    VersionChanged,

    /// <summary>The element is deleted in the database, or no longer exists at all.</summary>
    Deleted,
}

/// <summary>
/// An element whose change conflicts with the database, with its current database value, version and deleted flag.
/// Value and version are null when the element no longer exists.
/// </summary>
public sealed record NodeConflict(Guid Id, ConflictReason Reason, string? Value, uint? Version, bool IsDeleted);

/// <summary>Members Apply adds to its RFC 9457 problem responses.</summary>
public static class ApplyProblem
{
    /// <summary>The 409 problem's list of <see cref="NodeConflict"/>s.</summary>
    public const string ConflictsMember = "conflicts";
}
