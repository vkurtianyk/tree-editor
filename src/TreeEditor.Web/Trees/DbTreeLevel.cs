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

    /// <summary>
    /// The first page failed, so the level has no cursor and no "Load more" to try again with; the view offers a retry.
    /// A later page that fails keeps its cursor, so "Load more" retries it.
    /// </summary>
    public bool FirstPageFailed => !IsLoaded && !IsLoading && Error is not null;

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

    /// <summary>
    /// Loads this level again as a new one, as many rows as it has loaded, and expands again the rows expanded here,
    /// their levels reloaded the same way (one request per page, level by level). So a reload after an Apply or a
    /// Reset shows the database as it is now without collapsing the tree. Collapsed levels are dropped and load
    /// fresh on the next expand.
    /// </summary>
    public async Task<DbTreeLevel> ReloadAsync(TreeApiClient api, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var fresh = new DbTreeLevel(ParentId);
        do
        {
            await fresh.LoadNextPageAsync(api, cancellationToken);
        }
        while (fresh.Error is null && fresh.HasMore && fresh.nodes.Count < nodes.Count);

        var expanded = nodes
            .Where(node => node.IsExpanded && node.Children is not null)
            .ToDictionary(node => node.Item.Id, node => node.Children!);
        foreach (var node in fresh.nodes)
        {
            if (node.Item.HasChildren && expanded.TryGetValue(node.Item.Id, out var children))
            {
                node.Expand(await children.ReloadAsync(api, cancellationToken));
            }
        }

        return fresh;
    }

    /// <summary>The row with the id among the loaded rows of this level and the levels below it; null when none is.</summary>
    public NodeListItem? Find(Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Item.Id == id)
            {
                return node.Item;
            }

            if (node.Children?.Find(id) is { } found)
            {
                return found;
            }
        }

        return null;
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

    /// <summary>Expands the row with children a reload loaded already.</summary>
    internal void Expand(DbTreeLevel children)
    {
        IsExpanded = true;
        Children = children;
    }

    public void Collapse() => IsExpanded = false;
}
