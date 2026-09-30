namespace TreeEditor.Web.Cache;

/// <summary>
/// The browser's local cache of elements. Components render its queries and forward commands to it;
/// it reaches the server only through <see cref="ICacheApiClient"/>.
/// </summary>
public sealed class LocalCache(ICacheApiClient api)
{
    private readonly Dictionary<Guid, CachedElement> elements = [];
    private IReadOnlyList<CachedTreeRow>? viewTree;

    /// <summary>Raised after every change of what the queries return.</summary>
    public event Action? Changed;

    /// <summary>
    /// The cached elements in their hierarchy. Each element hangs under its nearest cached ancestor; ancestors
    /// that aren't cached show as placeholder rows. Built on first read after a change.
    /// </summary>
    public IReadOnlyList<CachedTreeRow> ViewTree => viewTree ??= ViewTreeBuilder.Build(elements.Values);

    public bool IsCached(Guid id) => elements.ContainsKey(id);

    /// <summary>
    /// Loads an element into the cache through load node. An element that is already cached is left as it is and
    /// not requested again, so a reload can never overwrite it. Load errors propagate and cache nothing.
    /// </summary>
    public async Task LoadElementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (elements.ContainsKey(id))
        {
            return;
        }

        var node = await api.LoadNodeAsync(id, cancellationToken);

        // Another load of the same id may have finished while this one was waiting; the first copy stays.
        if (!elements.TryAdd(id, CachedElement.From(node)))
        {
            return;
        }

        viewTree = null;
        Changed?.Invoke();
    }
}
