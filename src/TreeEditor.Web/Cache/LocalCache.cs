using TreeEditor.Contracts;
using TreeEditor.Domain;

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

    public bool HasPendingChanges => state.Originals.Count > 0;

    /// <summary>An Apply request is in flight. Only one is sent at a time; edits and discard wait for it.</summary>
    public bool IsApplying => state.IsApplying;

    public bool CanApply => HasPendingChanges && !IsApplying;

    /// <summary>
    /// The cached elements in their hierarchy. Each element hangs under its nearest cached ancestor; ancestors
    /// that aren't cached show as placeholder rows. Built on first read after a change.
    /// </summary>
    public IReadOnlyList<CachedTreeRow> ViewTree => state.ViewTree ??= ViewTreeBuilder.Build(state.Elements.Values);

    public bool IsCached(Guid id) => state.Elements.ContainsKey(id);

    /// <summary>The cached element with the id, with any pending change; null when it isn't cached.</summary>
    public CachedElement? Find(Guid id) => state.Elements.GetValueOrDefault(id);

    /// <summary>
    /// Loads an element into the cache through load node. An element that is already cached is left as it is and
    /// not requested again, so a reload can never overwrite it. Load errors propagate and cache nothing.
    /// A load that finishes after <see cref="Clear"/> caches nothing either: it read the database before the clear.
    /// An element loaded below a cached deleted ancestor is deleted too (see <see cref="DeleteBelowDeletedAncestor"/>).
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

        DeleteBelowDeletedAncestor(state.Elements[id]);
        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Changes an element's value locally; nothing is sent until <see cref="ApplyAsync"/>. The value is trimmed and
    /// validated at once: an invalid one is returned as an error and changes nothing. A value colliding with a cached
    /// live sibling gets the first free " (n)" suffix; the server resolves again against every sibling on Apply.
    /// Changing the value back to the loaded one leaves nothing pending.
    /// </summary>
    public ValueEditResult EditValue(Guid id, string? value)
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Elements can't be edited while an Apply is in flight.");
        }

        if (!state.Elements.TryGetValue(id, out var element))
        {
            throw new InvalidOperationException($"The element {id} isn't cached.");
        }

        if (element.IsDeleted)
        {
            throw new InvalidOperationException($"The element {id} is deleted and can't be edited.");
        }

        if (!ElementValue.TryNormalize(value, out var normalized, out var error))
        {
            return new ValueEditResult(Value: null, error);
        }

        var siblings = state.Elements.Values
            .Where(sibling => sibling.ParentId == element.ParentId)
            .Select(sibling => new SiblingValue(sibling.Id, sibling.Value, sibling.IsDeleted));
        var resolved = SiblingSuffix.Resolve(normalized, siblings, self: id);

        var original = state.Originals.GetValueOrDefault(id, element);
        if (resolved == original.Value)
        {
            state.Originals.Remove(id);
            state.Elements[id] = original;
        }
        else
        {
            state.Originals.TryAdd(id, element);
            state.Elements[id] = element with { Value = resolved, State = ElementState.Edited };
        }

        state.ViewTree = null;
        Changed?.Invoke();
        return new ValueEditResult(resolved, Error: null);
    }

    /// <summary>
    /// Deletes an element locally with every cached descendant, found by ancestors, so also below placeholders;
    /// nothing is sent until <see cref="ApplyAsync"/>. Their pending edits are dropped: they show their loaded values.
    /// Elements added locally are removed, as they never reached the database; descendants already deleted there
    /// stay as they are.
    /// </summary>
    public void Delete(Guid id)
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Elements can't be deleted while an Apply is in flight.");
        }

        if (!state.Elements.TryGetValue(id, out var element))
        {
            throw new InvalidOperationException($"The element {id} isn't cached.");
        }

        if (element.IsDeleted)
        {
            throw new InvalidOperationException($"The element {id} is already deleted.");
        }

        var subtree = state.Elements.Values
            .Where(member => member.Id == id || Ancestry.IsDescendantOf(member.Ancestors, id))
            .ToList();
        foreach (var member in subtree)
        {
            if (member.State == ElementState.New)
            {
                state.Elements.Remove(member.Id);
                state.Originals.Remove(member.Id);
            }
            else if (!member.IsDeleted)
            {
                MarkPendingDeleted(member);
            }
        }

        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops every pending change: changed elements go back to their loaded state, and clean elements stay cached.
    /// Nothing is sent to the server.
    /// </summary>
    public void DiscardAll()
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Changes can't be discarded while an Apply is in flight.");
        }

        if (state.Originals.Count == 0)
        {
            return;
        }

        foreach (var (id, original) in state.Originals)
        {
            state.Elements[id] = original;
        }

        state.Originals.Clear();
        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Sends every pending change in one request, each edit and delete with the version it was loaded with. Only the
    /// topmost deletes go: the server's cascade covers their descendants. On success the final values and versions
    /// are stored and nothing is pending any more. On an error everything stays pending and the error propagates.
    /// Does nothing when <see cref="CanApply"/> is false. A response that arrives after <see cref="Clear"/> stores
    /// nothing: the elements it was for are gone.
    /// </summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply)
        {
            return;
        }

        var edits = state.Originals.Keys
            .Select(id => state.Elements[id])
            .Where(element => element.State == ElementState.Edited)
            .Select(element => new NodeEdit(element.Id, element.Value, element.Version))
            .ToList();
        var deletes = state.Originals.Keys
            .Select(id => state.Elements[id])
            .Where(element => element.State == ElementState.Deleted && !HasPendingDeletedAncestor(element))
            .Select(element => new NodeDelete(element.Id, element.Version))
            .ToList();
        var request = new ApplyRequest(Inserts: [], edits, deletes);

        var applyingIn = state;
        applyingIn.IsApplying = true;
        Changed?.Invoke();
        try
        {
            var response = await api.ApplyAsync(request, cancellationToken);
            if (applyingIn == state)
            {
                StoreApplied(response);
            }
        }
        finally
        {
            applyingIn.IsApplying = false;
            Changed?.Invoke();
        }
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
    /// The applied elements become clean, with the server's final values and versions. Cached descendants of the
    /// deleted ones are deleted too, like the server's cascade.
    /// </summary>
    private void StoreApplied(ApplyResponse response)
    {
        foreach (var id in state.Originals.Keys)
        {
            state.Elements[id] = state.Elements[id] with { State = ElementState.Clean };
        }

        foreach (var node in response.Nodes)
        {
            if (state.Elements.TryGetValue(node.Id, out var element))
            {
                state.Elements[node.Id] = element with
                {
                    Value = node.Value,
                    Version = node.Version,
                    IsDeleted = node.IsDeleted,
                    State = ElementState.Clean,
                };
            }
        }

        foreach (var node in response.Nodes.Where(node => node.IsDeleted))
        {
            StoreDeletedSubtree(node.Id);
        }

        state.Originals.Clear();
        state.ViewTree = null;
    }

    /// <summary>Records a pending delete: the loaded copy is kept, and any pending edit is dropped.</summary>
    private void MarkPendingDeleted(CachedElement element)
    {
        var loaded = state.Originals.GetValueOrDefault(element.Id, element);
        state.Originals.TryAdd(element.Id, loaded);
        state.Elements[element.Id] = loaded with { IsDeleted = true, State = ElementState.Deleted };
    }

    private bool HasPendingDeletedAncestor(CachedElement element) =>
        element.Ancestors
            .Where(ancestorId => ancestorId != element.Id)
            .Any(ancestorId => state.Elements.GetValueOrDefault(ancestorId)?.State == ElementState.Deleted);

    /// <summary>
    /// The cache never holds a live element below a deleted one. Below a pending delete, a newly loaded element
    /// becomes a pending delete as well. Below an element deleted in the database, it was read before that delete
    /// committed, so it is deleted there by now.
    /// </summary>
    private void DeleteBelowDeletedAncestor(CachedElement element)
    {
        if (element.IsDeleted)
        {
            return;
        }

        var deletedAncestors = element.Ancestors
            .Where(ancestorId => ancestorId != element.Id)
            .Select(ancestorId => state.Elements.GetValueOrDefault(ancestorId))
            .OfType<CachedElement>()
            .Where(ancestor => ancestor.IsDeleted)
            .ToList();
        if (deletedAncestors.Count == 0)
        {
            return;
        }

        if (deletedAncestors.TrueForAll(ancestor => ancestor.State == ElementState.Deleted))
        {
            MarkPendingDeleted(element);
        }
        else
        {
            state.Elements[element.Id] = element with { IsDeleted = true };
        }
    }

    /// <summary>The database deleted the element's subtree: every cached descendant is deleted, with nothing pending.</summary>
    private void StoreDeletedSubtree(Guid id)
    {
        var descendants = state.Elements.Values
            .Where(element => Ancestry.IsDescendantOf(element.Ancestors, id))
            .ToList();
        foreach (var descendant in descendants)
        {
            state.Originals.Remove(descendant.Id);
            state.Elements[descendant.Id] = descendant with { IsDeleted = true, State = ElementState.Clean };
        }
    }

    /// <summary>
    /// Everything the cache holds. <see cref="Clear"/> replaces it as a whole, so any state added here is
    /// dropped with it.
    /// </summary>
    private sealed class CacheState
    {
        public Dictionary<Guid, CachedElement> Elements { get; } = [];

        /// <summary>
        /// The loaded copy of every element with a pending change, in the order they were first changed.
        /// </summary>
        public OrderedDictionary<Guid, CachedElement> Originals { get; } = [];

        /// <summary>An Apply request is in flight for these elements.</summary>
        public bool IsApplying { get; set; }

        /// <summary>Built from <see cref="Elements"/> on first read; reset to null on every change.</summary>
        public IReadOnlyList<CachedTreeRow>? ViewTree { get; set; }
    }
}
