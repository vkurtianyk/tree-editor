namespace TreeEditor.Contracts;

/// <summary>Route templates shared by the Api (mapping) and Web (client).</summary>
public static class ApiRoutes
{
    public const string Nodes = "/api/nodes";

    /// <summary>Load node: one element by id; 404 problem when no element has it.</summary>
    public const string Node = Nodes + "/{id:guid}";

    /// <summary>
    /// List children. Query: <c>parentId</c> (absent = roots), and the keyset cursor
    /// <c>afterValue</c> + <c>afterId</c> (both or neither; absent = first page).
    /// </summary>
    public const string Children = Nodes + "/children";

    /// <summary>
    /// Apply (POST): every pending change in one all-or-nothing transaction. 400 problem for a malformed request,
    /// 409 problem listing the conflicting elements.
    /// </summary>
    public const string Apply = "/api/apply";

    /// <summary>Reset (POST): restores the sample data; 204 when done.</summary>
    public const string Reset = "/api/reset";

    public static string NodeById(Guid id) => $"{Nodes}/{id}";

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
