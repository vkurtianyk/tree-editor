using TreeEditor.Data;
using TreeEditor.Domain;

namespace TreeEditor.Api.Tests.Harness;

/// <summary>
/// Describes a small tree for a test to arrange. Ids are fresh GUID v7s unless given, and ancestor lists
/// come from the same Domain rules the app uses.
/// </summary>
public sealed class TreeBuilder
{
    private readonly List<Node> nodes = [];

    public IReadOnlyList<Node> Nodes => nodes;

    public Node Root(string value, bool deleted = false, Guid? id = null)
    {
        var nodeId = id ?? Guid.CreateVersion7();
        return Add(new Node
        {
            Id = nodeId,
            Ancestors = Ancestry.ForRoot(nodeId),
            Value = value,
            IsDeleted = deleted,
        });
    }

    public Node Child(Node parent, string value, bool deleted = false, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var nodeId = id ?? Guid.CreateVersion7();
        return Add(new Node
        {
            Id = nodeId,
            ParentId = parent.Id,
            Ancestors = Ancestry.ForChild(parent.Ancestors, nodeId),
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
