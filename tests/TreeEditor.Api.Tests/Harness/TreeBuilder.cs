using TreeEditor.Data;
using TreeEditor.Domain;

namespace TreeEditor.Api.Tests.Harness;

/// <summary>
/// Describes a small tree for a test to arrange. Ids are fresh GUID v7s and ancestor lists
/// come from the same Domain rules the app uses.
/// </summary>
public sealed class TreeBuilder
{
    private readonly List<Node> nodes = [];

    public IReadOnlyList<Node> Nodes => nodes;

    public Node Root(string value, bool deleted = false)
    {
        var id = Guid.CreateVersion7();
        return Add(new Node
        {
            Id = id,
            Ancestors = Ancestry.ForRoot(id),
            Value = value,
            IsDeleted = deleted,
        });
    }

    public Node Child(Node parent, string value, bool deleted = false)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var id = Guid.CreateVersion7();
        return Add(new Node
        {
            Id = id,
            ParentId = parent.Id,
            Ancestors = Ancestry.ForChild(parent.Ancestors, id),
            Value = value,
            IsDeleted = deleted,
        });
    }

    private Node Add(Node node)
    {
        nodes.Add(node);
        return node;
    }
}
