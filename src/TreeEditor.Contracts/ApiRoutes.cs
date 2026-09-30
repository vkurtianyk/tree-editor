namespace TreeEditor.Contracts;

/// <summary>Route templates shared by the Api (mapping) and Web (client).</summary>
public static class ApiRoutes
{
    public const string Nodes = "/api/nodes";

    /// <summary>List children. Query: <c>parentId</c> (absent = roots).</summary>
    public const string Children = Nodes + "/children";

    public static string ChildrenOf(Guid? parentId) =>
        parentId is { } id ? $"{Children}?parentId={id}" : Children;
}
