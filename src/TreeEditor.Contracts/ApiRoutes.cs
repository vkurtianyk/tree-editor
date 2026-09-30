namespace TreeEditor.Contracts;

/// <summary>Route templates shared by the Api (mapping) and Web (client).</summary>
public static class ApiRoutes
{
    public const string Nodes = "/api/nodes";

    /// <summary>
    /// List children. Query: <c>parentId</c> (absent = roots), and the keyset cursor
    /// <c>afterValue</c> + <c>afterId</c> (both or neither; absent = first page).
    /// </summary>
    public const string Children = Nodes + "/children";

    public static string ChildrenOf(Guid? parentId, ChildrenCursor? after = null)
    {
        var query = new List<string>(3);
        if (parentId is { } id)
        {
            query.Add($"parentId={id}");
        }

        if (after is not null)
        {
            query.Add($"afterValue={Uri.EscapeDataString(after.LowerValue)}");
            query.Add($"afterId={after.Id}");
        }

        return query.Count == 0 ? Children : $"{Children}?{string.Join('&', query)}";
    }
}
