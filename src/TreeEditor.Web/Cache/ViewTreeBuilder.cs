namespace TreeEditor.Web.Cache;

/// <summary>
/// Builds CachedTreeView's rows from the ancestor lists of the cached elements. Every path is merged into one forest
/// of ids; ids that aren't cached become placeholders, collapsed only along lines without branches.
/// </summary>
internal static class ViewTreeBuilder
{
    public static IReadOnlyList<CachedTreeRow> Build(IEnumerable<CachedElement> elements)
    {
        var nodes = new Dictionary<Guid, PathNode>();
        var roots = new List<PathNode>();
        foreach (var element in elements)
        {
            PathNode? parent = null;
            foreach (var id in element.Ancestors)
            {
                if (!nodes.TryGetValue(id, out var node))
                {
                    // Parents never change, so an id is linked once, under the id before it on the first path seen.
                    node = new PathNode(id);
                    nodes.Add(id, node);
                    (parent?.Children ?? roots).Add(node);
                }

                parent = node;
            }

            parent!.Element = element;
        }

        return BuildRows(roots);
    }

    private static CachedTreeRow[] BuildRows(List<PathNode> siblings)
    {
        var rows = siblings.Select(BuildRow).ToArray();
        Array.Sort(rows, CompareSiblings);
        return rows;
    }

    private static CachedTreeRow BuildRow(PathNode node)
    {
        if (node.Element is { } element)
        {
            return new CachedElementRow(element, BuildRows(node.Children));
        }

        // A missing id only exists as an ancestor of a cached element, so it has at least one child. At a branch
        // point (two or more) it is a row of its own; otherwise the run takes in missing ids down the line until
        // the next cached element or branch point.
        var missing = new List<Guid> { node.Id };
        var last = node;
        while (last.Children is [{ Element: null, Children.Count: 1 } next])
        {
            missing.Add(next.Id);
            last = next;
        }

        return new PlaceholderRow(missing, BuildRows(last.Children));
    }

    /// <summary>
    /// Elements first, like DBTreeView: case-insensitive value, then id. Then placeholders by their top missing id.
    /// </summary>
    private static int CompareSiblings(CachedTreeRow x, CachedTreeRow y) => (x, y) switch
    {
        (CachedElementRow a, CachedElementRow b) => CompareElements(a.Element, b.Element),
        (CachedElementRow, PlaceholderRow) => -1,
        (PlaceholderRow, CachedElementRow) => 1,
        _ => x.Key.CompareTo(y.Key),
    };

    private static int CompareElements(CachedElement a, CachedElement b)
    {
        var byValue = string.CompareOrdinal(a.Value.ToLowerInvariant(), b.Value.ToLowerInvariant());
        return byValue != 0 ? byValue : a.Id.CompareTo(b.Id);
    }

    private sealed class PathNode(Guid id)
    {
        public Guid Id { get; } = id;

        public CachedElement? Element { get; set; }

        public List<PathNode> Children { get; } = [];
    }
}
