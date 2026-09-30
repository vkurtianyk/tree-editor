using TreeEditor.Web.Cache;

namespace TreeEditor.Web.Tests.Cache;

/// <summary>
/// A view tree as indented lines, two spaces per level. Elements show their value (plus " [deleted]");
/// placeholders show "… count (first..last)" with the names of the missing ancestors, or "… 1 (name)".
/// </summary>
internal static class Outline
{
    public static string[] Of(IReadOnlyList<CachedTreeRow> rows, TestTree tree)
    {
        var lines = new List<string>();
        Append(rows, 0);
        return [.. lines];

        void Append(IReadOnlyList<CachedTreeRow> level, int depth)
        {
            foreach (var row in level)
            {
                var indent = new string(' ', depth * 2);
                lines.Add(row switch
                {
                    CachedElementRow { Element: var element } =>
                        $"{indent}{element.Value}{(element.IsDeleted ? " [deleted]" : "")}",
                    PlaceholderRow { MissingIds: var missing } when missing.Count == 1 =>
                        $"{indent}… 1 ({tree.NameOf(missing[0])})",
                    PlaceholderRow { MissingIds: var missing } =>
                        $"{indent}… {missing.Count} ({tree.NameOf(missing[0])}..{tree.NameOf(missing[^1])})",
                    _ => throw new InvalidOperationException($"Unknown row type {row.GetType().Name}."),
                });
                Append(row.Children, depth + 1);
            }
        }
    }
}
