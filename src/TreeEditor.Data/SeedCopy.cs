using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TreeEditor.Data;

public static class SeedCopy
{
    /// <summary>
    /// Fills <c>nodes</c> from <c>seed_nodes</c> inside Postgres (first fill and Reset).
    /// Parents are inserted before children so the parent FK holds row by row.
    /// </summary>
    public static Task<int> FillNodesFromSeedAsync(this DatabaseFacade database, CancellationToken cancellationToken) =>
        database.ExecuteSqlRawAsync(
            """
            INSERT INTO nodes (id, parent_id, ancestors, value, is_deleted)
            SELECT id, parent_id, ancestors, value, is_deleted
            FROM seed_nodes
            ORDER BY cardinality(ancestors)
            """,
            cancellationToken);
}
