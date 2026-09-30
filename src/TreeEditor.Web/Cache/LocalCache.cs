namespace TreeEditor.Web.Cache;

/// <summary>
/// The browser's local cache of elements. Components render its queries and forward commands to it;
/// it reaches the server only through <see cref="ICacheApiClient"/>.
/// </summary>
public sealed class LocalCache(ICacheApiClient api)
{
    private CacheState state = new();

    /// <summary>Raised after every change of what the queries return.</summary>
    public event Action? Changed;

    /// <summary>
    /// The cached elements in their hierarchy. Each element hangs under its nearest cached ancestor; ancestors
    /// that aren't cached show as placeholder rows. Built on first read after a change.
    /// </summary>
    public IReadOnlyList<CachedTreeRow> ViewTree => state.ViewTree ??= ViewTreeBuilder.Build(state.Elements.Values);

    public bool IsCached(Guid id) => state.Elements.ContainsKey(id);

    /// <summary>
    /// Loads an element into the cache through load node. An element that is already cached is left as it is and
    /// not requested again, so a reload can never overwrite it. Load errors propagate and cache nothing.
    /// A load that finishes after <see cref="Clear"/> caches nothing either: it read the database before the clear.
    /// </summary>
    public async Task LoadElementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (state.Elements.ContainsKey(id))
        {
            return;
        }

        var loadingInto = state;
        var node = await api.LoadNodeAsync(id, cancellationToken);

        // Another load of the same id may have finished while this one was waiting; the first copy stays.
        if (loadingInto != state || !state.Elements.TryAdd(id, CachedElement.From(node)))
        {
            return;
        }

        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Empties the cache: every element and everything pending on them is dropped, with no request to the server.
    /// Used by Reset, after which nothing cached matches the database any more.
    /// </summary>
    public void Clear()
    {
        state = new CacheState();
        Changed?.Invoke();
    }

    /// <summary>
    /// Everything the cache holds. <see cref="Clear"/> replaces it as a whole, so any state added here is
    /// dropped with it.
    /// </summary>
    private sealed class CacheState
    {
        public Dictionary<Guid, CachedElement> Elements { get; } = [];

        /// <summary>Built from <see cref="Elements"/> on first read; reset to null on every change.</summary>
        public IReadOnlyList<CachedTreeRow>? ViewTree { get; set; }
    }
}
