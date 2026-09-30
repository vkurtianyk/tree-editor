namespace TreeEditor.Contracts;

/// <summary>One row of a children listing.</summary>
public sealed record NodeListItem(Guid Id, string Value, bool IsDeleted, bool HasChildren);

/// <summary>A page of children ordered by lowercase value, then id.</summary>
public sealed record ChildrenPage(IReadOnlyList<NodeListItem> Items, bool HasMore)
{
    public const int PageSize = 100;
}
