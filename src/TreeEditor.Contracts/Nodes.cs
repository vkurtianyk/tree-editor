namespace TreeEditor.Contracts;

/// <summary>One row of a children listing.</summary>
public sealed record NodeListItem(Guid Id, string Value, bool IsDeleted, bool HasChildren);

/// <summary>
/// One element as the cache loads it. <see cref="Ancestors"/> is the stored ancestor list: ancestor ids root first,
/// ending with the element's own id. <see cref="Version"/> is the row version edits and deletes are checked against.
/// </summary>
public sealed record NodeDetails(
    Guid Id,
    Guid? ParentId,
    IReadOnlyList<Guid> Ancestors,
    string Value,
    uint Version,
    bool IsDeleted);

/// <summary>
/// Keyset position in a children listing: the lowercase value (as the database lowercases it) and id
/// of the last row seen. The next page starts right after it.
/// </summary>
public sealed record ChildrenCursor(string LowerValue, Guid Id);

/// <summary>
/// A page of children ordered by lowercase value, then id. <see cref="Next"/> continues the listing;
/// it is set exactly when <see cref="HasMore"/> is.
/// </summary>
public sealed record ChildrenPage(IReadOnlyList<NodeListItem> Items, bool HasMore, ChildrenCursor? Next)
{
    public const int PageSize = 100;
}
