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
