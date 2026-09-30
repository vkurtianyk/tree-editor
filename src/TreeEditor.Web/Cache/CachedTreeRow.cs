using TreeEditor.Contracts;

namespace TreeEditor.Web.Cache;

/// <summary>An element held by the <see cref="LocalCache"/>: as it was loaded, plus any pending local change.</summary>
public sealed record CachedElement(
    Guid Id,
    Guid? ParentId,
    IReadOnlyList<Guid> Ancestors,
    string Value,
    uint Version,
    bool IsDeleted)
{
    /// <summary>Whether the element carries a pending change; its version stays the loaded one until Apply.</summary>
    public ElementState State { get; init; }

    /// <summary>
    /// Set when the last Apply found the pending change in conflict with the database: the database's value and
    /// version. It stays until the user takes the database copy or keeps theirs.
    /// </summary>
    public NodeConflict? Conflict { get; init; }

    public static CachedElement From(NodeDetails node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new CachedElement(node.Id, node.ParentId, node.Ancestors, node.Value, node.Version, node.IsDeleted);
    }
}

/// <summary>A row of CachedTreeView: a cached element or a placeholder for ancestors that aren't cached.</summary>
public abstract record CachedTreeRow(IReadOnlyList<CachedTreeRow> Children)
{
    /// <summary>Unique among all rows of a view tree: the element's id, or a placeholder's first missing id.</summary>
    public abstract Guid Key { get; }
}

public sealed record CachedElementRow(CachedElement Element, IReadOnlyList<CachedTreeRow> Children)
    : CachedTreeRow(Children)
{
    public override Guid Key => Element.Id;
}

/// <summary>
/// Consecutive ancestors that aren't cached, top first. Several levels collapse into one row only on a single line;
/// a missing ancestor where cached branches diverge is a row of its own, which the branches share.
/// </summary>
public sealed record PlaceholderRow(IReadOnlyList<Guid> MissingIds, IReadOnlyList<CachedTreeRow> Children)
    : CachedTreeRow(Children)
{
    public int MissingLevels => MissingIds.Count;

    public override Guid Key => MissingIds[0];
}
