namespace TreeEditor.Domain;

/// <summary>
/// Ancestor lists: every element stores its ancestor ids plus its own id, root first.
/// A root's list is just its own id; a child's list is its parent's list plus its own id.
/// </summary>
public static class Ancestry
{
    public static Guid[] ForRoot(Guid id)
    {
        EnsureId(id);
        return [id];
    }

    public static Guid[] ForChild(IReadOnlyList<Guid> parentAncestors, Guid id)
    {
        ArgumentNullException.ThrowIfNull(parentAncestors);
        if (parentAncestors.Count == 0)
        {
            throw new ArgumentException("A parent's ancestor list contains at least the parent itself.", nameof(parentAncestors));
        }

        EnsureId(id);
        if (parentAncestors.Contains(id))
        {
            throw new ArgumentException("An element cannot be its own ancestor.", nameof(id));
        }

        var ancestors = new Guid[parentAncestors.Count + 1];
        for (var i = 0; i < parentAncestors.Count; i++)
        {
            ancestors[i] = parentAncestors[i];
        }

        ancestors[^1] = id;
        return ancestors;
    }

    public static Guid RootOf(IReadOnlyList<Guid> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        return ancestors.Count > 0
            ? ancestors[0]
            : throw new ArgumentException("An ancestor list contains at least the element itself.", nameof(ancestors));
    }

    /// <summary>
    /// Whether the element with this ancestor list lies in the subtree below <paramref name="ancestorId"/>:
    /// the id appears on its path before the element itself. An element is not its own descendant.
    /// </summary>
    public static bool IsDescendantOf(IReadOnlyList<Guid> ancestors, Guid ancestorId)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        if (ancestors.Count == 0)
        {
            throw new ArgumentException("An ancestor list contains at least the element itself.", nameof(ancestors));
        }

        for (var i = 0; i < ancestors.Count - 1; i++)
        {
            if (ancestors[i] == ancestorId)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An element id must not be empty.", nameof(id));
        }
    }
}
