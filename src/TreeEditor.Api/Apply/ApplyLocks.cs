using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using TreeEditor.Data;

namespace TreeEditor.Api.Apply;

/// <summary>
/// The Postgres advisory locks that make Applies on the same root tree run one at a time: one lock per touched root,
/// plus one shared roots lock when an Apply renames a root, because roots are siblings of each other across trees.
/// Transaction-scoped, so a commit or rollback releases them. Applies on different root trees don't wait for each other.
/// </summary>
public static class ApplyLocks
{
    /// <summary>
    /// The roots lock. The smallest key, so every Apply takes it before its root locks: one order for all locks.
    /// </summary>
    public const long RootsKey = long.MinValue;

    /// <summary>
    /// A root tree's lock key: the id's two 64-bit halves XORed. Roots that share a key only wait for each other
    /// needlessly, and a key equal to <see cref="RootsKey"/> merges with it.
    /// </summary>
    public static long KeyOf(Guid rootId)
    {
        Span<byte> bytes = stackalloc byte[16];
        rootId.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadInt64BigEndian(bytes) ^ BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
    }

    /// <summary>
    /// The keys an Apply locks, in the order it takes them: distinct and ascending, so two Applies never each hold a
    /// lock the other waits for. The roots lock, when taken, comes first.
    /// </summary>
    public static IReadOnlyList<long> KeysFor(IEnumerable<Guid> roots, bool renamesRoot)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var keys = roots.Select(KeyOf);
        return [.. (renamesRoot ? keys.Append(RootsKey) : keys).Distinct().Order()];
    }

    /// <summary>
    /// Takes the locks inside the current transaction, waiting while another transaction holds any of them.
    /// Take them before the first read, so the reads see what the Apply that held them committed.
    /// </summary>
    public static async Task LockAsync(
        TreeDbContext db,
        IEnumerable<Guid> roots,
        bool renamesRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            // Outside a transaction a transaction-scoped lock is released as soon as its statement ends.
            throw new InvalidOperationException("Apply locks are taken inside the Apply transaction.");
        }

        // One statement per lock, so they're taken in exactly this order.
        foreach (var key in KeysFor(roots, renamesRoot))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        }
    }
}
