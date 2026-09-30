using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using TreeEditor.Contracts;
using TreeEditor.Domain;

namespace TreeEditor.Api.Caching;

/// <summary>
/// The backend read cache: children pages and node loads in HybridCache for <see cref="Expiration"/>.
/// Every entry is tagged with the root of each element it shows, and the roots listing also with a shared roots tag,
/// so a commit invalidates exactly the root trees it touched. Unknown elements aren't cached.
/// </summary>
public sealed class ReadCache(HybridCache cache)
{
    /// <summary>Bounds how long an entry stored by a read that raced a commit stays stale.</summary>
    public static readonly TimeSpan Expiration = TimeSpan.FromSeconds(60);

    private const string RootsTag = "roots";

    private static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = Expiration,
        LocalCacheExpiration = Expiration,
    };

    // HybridCache takes an entry's tags before its factory runs, but the root is known only from the data read.
    // So the factory stores the entry itself with its tags, and the lookup around it never writes: it only reads
    // and keeps concurrent misses of one key down to a single database query.
    private static readonly HybridCacheEntryOptions ReadThrough = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    /// <summary>A node load, tagged with the node's root; null (and not cached) for an unknown id.</summary>
    public ValueTask<NodeDetails?> GetNodeAsync(
        Guid id,
        Func<CancellationToken, Task<NodeDetails?>> load,
        CancellationToken cancellationToken) =>
        GetOrLoadAsync<NodeDetails?>(
            $"node:{id}",
            async ct =>
            {
                var node = await load(ct);
                return (node, node is null ? null : [RootTag(Ancestry.RootOf(node.Ancestors))]);
            },
            cancellationToken);

    /// <summary>
    /// A page of the roots, tagged with the roots tag (a renamed root moves between pages) and with each listed root's
    /// tag (its deleted flag and hasChildren change with its tree).
    /// </summary>
    public ValueTask<ChildrenPage> GetRootsAsync(
        ChildrenCursor? after,
        Func<CancellationToken, Task<ChildrenPage>> load,
        CancellationToken cancellationToken) =>
        GetOrLoadAsync<ChildrenPage>(
            ChildrenKey(parentId: null, after),
            async ct =>
            {
                var page = await load(ct);
                return (page, [RootsTag, .. page.Items.Select(item => RootTag(item.Id))]);
            },
            cancellationToken);

    /// <summary>A page of a parent's children, tagged with the parent's root; not cached for an unknown parent.</summary>
    public ValueTask<ChildrenPage> GetChildrenAsync(
        Guid parentId,
        ChildrenCursor? after,
        Func<CancellationToken, Task<Guid?>> loadParentRoot,
        Func<CancellationToken, Task<ChildrenPage>> load,
        CancellationToken cancellationToken) =>
        GetOrLoadAsync<ChildrenPage>(
            ChildrenKey(parentId, after),
            async ct =>
            {
                var root = await loadParentRoot(ct);
                var page = await load(ct);
                return (page, root is { } id ? [RootTag(id)] : null);
            },
            cancellationToken);

    /// <summary>
    /// After an Apply commits: the touched root trees, plus the roots listing when a touched element is a root itself.
    /// </summary>
    public ValueTask InvalidateAsync(IReadOnlyCollection<Guid> touchedRoots, IEnumerable<Guid> touchedIds)
    {
        List<string> tags = [.. touchedRoots.Select(RootTag)];
        if (touchedIds.Any(touchedRoots.Contains))
        {
            tags.Add(RootsTag);
        }

        // Not cancellable: the commit has happened, so the stale entries must go even if the request was cancelled.
        return cache.RemoveByTagAsync(tags, CancellationToken.None);
    }

    /// <summary>After a Reset commits: every entry.</summary>
    public ValueTask ClearAsync() => cache.RemoveByTagAsync("*", CancellationToken.None);

    private static string RootTag(Guid rootId) => $"root:{rootId}";

    private static string ChildrenKey(Guid? parentId, ChildrenCursor? after)
    {
        var parent = parentId is { } id ? id.ToString() : RootsTag;
        if (after is null)
        {
            return $"children:{parent}";
        }

        // The cursor value is client text of any length; hashed, it always makes a valid key.
        var value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(after.LowerValue)));
        return $"children:{parent}:{after.Id}:{value}";
    }

    /// <summary>The cached value, or the loaded one, stored with its tags unless they are null.</summary>
    private ValueTask<T> GetOrLoadAsync<T>(
        string key,
        Func<CancellationToken, Task<(T Value, string[]? Tags)>> load,
        CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            key,
            (Cache: cache, Key: key, Load: load),
            static async (state, ct) =>
            {
                var (value, tags) = await state.Load(ct);
                if (tags is not null)
                {
                    await state.Cache.SetAsync(state.Key, value, EntryOptions, tags, ct);
                }

                return value;
            },
            ReadThrough,
            cancellationToken: cancellationToken);
}
