using TreeEditor.Contracts;
using TreeEditor.Domain;

namespace TreeEditor.Web.Tests.Cache;

/// <summary>
/// The database side of a cache test: named elements with ids that ascend in creation order,
/// so a test controls how ids compare.
/// </summary>
internal sealed class TestTree
{
    private readonly Dictionary<Guid, NodeDetails> nodes = [];
    private readonly Dictionary<Guid, string> names = [];
    private int lastId;

    public NodeDetails Root(string value, bool deleted = false) =>
        Add(NextId(), null, [], value, deleted);

    public NodeDetails Child(NodeDetails parent, string value, bool deleted = false) =>
        Add(NextId(), parent.Id, parent.Ancestors, value, deleted);

    /// <summary>A line of <paramref name="count"/> elements below <paramref name="parent"/>, named prefix + level.</summary>
    public IReadOnlyList<NodeDetails> Chain(NodeDetails parent, string prefix, int firstLevel, int count)
    {
        var chain = new List<NodeDetails>(count);
        for (var level = firstLevel; level < firstLevel + count; level++)
        {
            parent = Child(parent, $"{prefix}{level}");
            chain.Add(parent);
        }

        return chain;
    }

    public bool TryGet(Guid id, out NodeDetails node) => nodes.TryGetValue(id, out node!);

    public string NameOf(Guid id) => names[id];

    private Guid NextId() => Guid.Parse($"0199a000-0000-7000-8000-{++lastId:x12}");

    private NodeDetails Add(Guid id, Guid? parentId, IReadOnlyList<Guid> parentAncestors, string value, bool deleted)
    {
        var ancestors = parentId is null ? Ancestry.ForRoot(id) : Ancestry.ForChild(parentAncestors, id);
        // Versions differ per element, like xmin, so a test can tell they were carried over.
        var node = new NodeDetails(id, parentId, ancestors, value, (uint)(1000 + lastId), deleted);
        nodes.Add(id, node);
        names.Add(id, value);
        return node;
    }
}
