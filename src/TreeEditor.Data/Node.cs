namespace TreeEditor.Data;

/// <summary>A row of <c>nodes</c>.</summary>
public sealed class Node
{
    /// <summary>GUID v7, final at creation.</summary>
    public Guid Id { get; set; }

    /// <summary>Null for roots.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Ancestor ids plus own id, root first. Written once.</summary>
    public Guid[] Ancestors { get; set; } = [];

    public string Value { get; set; } = "";

    public bool IsDeleted { get; set; }

    /// <summary>Postgres <c>xmin</c>: the element's version, used as the concurrency token.</summary>
    public uint Version { get; set; }
}

/// <summary>A row of <c>seed_nodes</c>: the generated sample data that <c>nodes</c> is filled from.</summary>
public sealed class SeedNode
{
    public Guid Id { get; set; }

    public Guid? ParentId { get; set; }

    public Guid[] Ancestors { get; set; } = [];

    public string Value { get; set; } = "";

    public bool IsDeleted { get; set; }
}
