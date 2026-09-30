using TreeEditor.Contracts;
using TreeEditor.Web.Api;

namespace TreeEditor.Web.Trees;

/// <summary>
/// The children of one parent in DBTreeView (the roots when the parent is null), loaded a page at a time.
/// Each page continues from the cursor the previous one returned.
/// </summary>
public sealed class DbTreeLevel(Guid? parentId)
{
    private readonly List<DbTreeNode> nodes = [];
    private readonly HashSet<Guid> listedIds = [];
    private ChildrenCursor? next;

    public Guid? ParentId { get; } = parentId;

    public IReadOnlyList<DbTreeNode> Nodes => nodes;

    /// <summary>The first page has arrived.</summary>
    public bool IsLoaded { get; private set; }

    public bool IsLoading { get; private set; }

    /// <summary>More children exist after the loaded ones.</summary>
    public bool HasMore => next is not null;

    /// <summary>Why the last load failed; cleared by the next load.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads the first page, or appends the next one. Does nothing while a load runs or when all are loaded.</summary>
    public async Task LoadNextPageAsync(TreeApiClient api, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (IsLoading || (IsLoaded && !HasMore))
        {
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            var page = await api.ListChildrenAsync(ParentId, next, cancellationToken);
            // A child renamed past the cursor since an earlier page comes again; it keeps the row it has, as
            // two rows with one id would break the view's @key.
            nodes.AddRange(page.Items.Where(item => listedIds.Add(item.Id)).Select(item => new DbTreeNode(item)));
            next = page.Next;
            IsLoaded = true;
        }
        catch (HttpRequestException ex)
        {
            Error = $"Could not load the elements: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>A row of DBTreeView.</summary>
public sealed class DbTreeNode(NodeListItem item)
{
    public NodeListItem Item { get; } = item;

    public bool IsExpanded { get; private set; }

    /// <summary>Created on the first expand and kept while collapsed, so expanding again needs no request.</summary>
    public DbTreeLevel? Children { get; private set; }

    /// <summary>Expands the row; returns its children level, which may still need its first page.</summary>
    public DbTreeLevel Expand()
    {
        IsExpanded = true;
        return Children ??= new DbTreeLevel(Item.Id);
    }

    public void Collapse() => IsExpanded = false;
}
