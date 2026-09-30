using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TreeEditor.Data;

namespace TreeEditor.MigrationService.Seeding;

/// <summary>Writes rows to <c>seed_nodes</c> with binary COPY (fast at any seed size).</summary>
public static class SeedNodeWriter
{
    public static async Task<ulong> CopyAsync(TreeDbContext db, IEnumerable<SeedRow> rows, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var importer = await connection.BeginBinaryImportAsync(
                "COPY seed_nodes (id, parent_id, ancestors, value, is_deleted) FROM STDIN (FORMAT BINARY)",
                cancellationToken);

            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid, cancellationToken);
                if (row.ParentId is { } parentId)
                {
                    await importer.WriteAsync(parentId, NpgsqlDbType.Uuid, cancellationToken);
                }
                else
                {
                    await importer.WriteNullAsync(cancellationToken);
                }

                await importer.WriteAsync(row.Ancestors, NpgsqlDbType.Array | NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(row.Value, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(false, NpgsqlDbType.Boolean, cancellationToken);
            }

            // Without CompleteAsync, disposing the importer cancels the COPY.
            return await importer.CompleteAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
