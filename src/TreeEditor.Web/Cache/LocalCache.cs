using System.Net;
using TreeEditor.Contracts;
using TreeEditor.Domain;
using TreeEditor.Web.Api;

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

    public bool HasPendingChanges => state.Originals.Count > 0 || state.NewIds.Count > 0;

    /// <summary>An Apply request is in flight. Only one is sent at a time; changes and discard wait for it.</summary>
    public bool IsApplying => state.IsApplying;

    /// <summary>
    /// Apply waits for every conflict to be resolved with <see cref="TakeDatabase"/> or <see cref="KeepMine"/>.
    /// </summary>
    public bool CanApply => HasPendingChanges && !IsApplying && !HasUnresolvedConflicts;

    public bool HasUnresolvedConflicts => state.ConflictIds.Count > 0;

    /// <summary>
    /// The elements whose pending change the last Apply found in conflict with the database, in the order the server
    /// listed them; each carries its <see cref="CachedElement.Conflict"/>.
    /// </summary>
    public IReadOnlyList<CachedElement> UnresolvedConflicts => [.. state.ConflictIds.Select(id => state.Elements[id])];

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
    /// An element that comes back deleted in the database takes its cached descendants with it, as the database's
    /// cascade did: their pending changes and conflicts are dropped and new ones are removed.
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

        var element = state.Elements[id];
        if (element.IsDeleted)
        {
            StoreDeletedBelow(id);
        }
        else
        {
            DeleteBelowDeletedAncestor(element);
        }

        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Changes an element's value locally; nothing is sent until <see cref="ApplyAsync"/>. The value is trimmed and
    /// validated at once: an invalid one is returned as an error and changes nothing. A value colliding with a cached
    /// live sibling gets the first free " (n)" suffix; the server resolves again against every sibling on Apply.
    /// Changing the value back to the loaded one leaves nothing pending. A new element stays new: its insert carries
    /// the latest value.
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

        ThrowIfConflicting(element);

        if (!ElementValue.TryNormalize(value, out var normalized, out var error))
        {
            return new ValueEditResult(Value: null, error);
        }

        var siblings = state.Elements.Values
            .Where(sibling => sibling.ParentId == element.ParentId)
            .Select(sibling => new SiblingValue(sibling.Id, sibling.Value, sibling.IsDeleted));
        var resolved = SiblingSuffix.Resolve(normalized, siblings, self: id);

        var original = state.Originals.GetValueOrDefault(id, element);
        if (element.State == ElementState.New)
        {
            state.Elements[id] = element with { Value = resolved };
        }
        else if (resolved == original.Value)
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
    /// Creates a child of a cached live element locally, with a new GUID v7 id that stays its id after Apply; nothing
    /// is sent until <see cref="ApplyAsync"/>. The parent may itself be new. The value is trimmed, validated and
    /// suffixed against cached live siblings like an edit; an invalid one is returned as an error and adds nothing.
    /// </summary>
    public AddChildResult AddChild(Guid parentId, string? value)
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Elements can't be added while an Apply is in flight.");
        }

        if (!state.Elements.TryGetValue(parentId, out var parent))
        {
            throw new InvalidOperationException($"The element {parentId} isn't cached.");
        }

        if (parent.IsDeleted)
        {
            throw new InvalidOperationException($"The element {parentId} is deleted and can't have children added.");
        }

        if (!ElementValue.TryNormalize(value, out var normalized, out var error))
        {
            return new AddChildResult(Id: null, Value: null, error);
        }

        var siblings = state.Elements.Values
            .Where(sibling => sibling.ParentId == parentId)
            .Select(sibling => new SiblingValue(sibling.Id, sibling.Value, sibling.IsDeleted));
        var resolved = SiblingSuffix.Resolve(normalized, siblings);

        var id = Guid.CreateVersion7();
        state.Elements.Add(
            id,
            new CachedElement(id, parentId, Ancestry.ForChild(parent.Ancestors, id), resolved, Version: 0, IsDeleted: false)
            {
                State = ElementState.New,
            });
        state.NewIds.Add(id);
        state.ViewTree = null;
        Changed?.Invoke();
        return new AddChildResult(id, resolved, Error: null);
    }

    /// <summary>
    /// Deletes an element locally with every cached descendant, found by ancestors, so also below placeholders;
    /// nothing is sent until <see cref="ApplyAsync"/>. Their pending edits are dropped: they show their loaded values.
    /// Elements added locally are removed, as they never reached the database; descendants already deleted there
    /// stay as they are. Conflicts of descendants are resolved by the delete: their changes are dropped or covered,
    /// and their database copy replaces the stale loaded one, so a discard goes back to it.
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

        ThrowIfConflicting(element);

        var subtree = state.Elements.Values
            .Where(member => member.Id == id || Ancestry.IsDescendantOf(member.Ancestors, id))
            .ToList();
        foreach (var member in subtree)
        {
            if (member.State == ElementState.New)
            {
                state.Elements.Remove(member.Id);
                state.NewIds.Remove(member.Id);
            }
            else if (member.Conflict is { } conflict)
            {
                // The conflict showed the loaded copy is stale. A conflicting pending delete below isn't sent any
                // more either: this delete covers it.
                state.Originals[member.Id] = DatabaseCopy(state.Originals[member.Id], conflict);
                MarkPendingDeleted(member);
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
    /// Drops every pending change: changed and deleted elements go back to their loaded state, new elements are
    /// removed, and clean elements stay cached. A conflicting element takes the database copy from its conflict
    /// instead, as the loaded one is known to be stale. Nothing is sent to the server.
    /// </summary>
    public void DiscardAll()
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Changes can't be discarded while an Apply is in flight.");
        }

        if (!HasPendingChanges)
        {
            return;
        }

        foreach (var (id, original) in state.Originals)
        {
            state.Elements[id] = state.Elements[id].Conflict is { } conflict
                ? DatabaseCopy(original, conflict)
                : original;
        }

        foreach (var id in state.NewIds)
        {
            state.Elements.Remove(id);
        }

        state.Originals.Clear();
        state.NewIds.Clear();
        state.ConflictIds.Clear();
        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Sends every pending change in one request: new elements as inserts in creation order (parents first), each edit
    /// and delete with the version it was loaded with. Only the topmost deletes go: the server's cascade covers their
    /// descendants. On success the final values and versions are stored, new elements keep their ids, and nothing is
    /// pending any more. On an error everything stays pending and the error propagates.
    /// On a conflict (an <see cref="ApplyRejectedException"/> with 409) the conflicts are stored before it
    /// propagates: an element deleted in the database is deleted here too, with its cached descendants, and its
    /// pending change is dropped; every other conflicting element keeps its change and carries the
    /// <see cref="CachedElement.Conflict"/> until <see cref="TakeDatabase"/> or <see cref="KeepMine"/> resolves it.
    /// Nothing is sent again by itself.
    /// Does nothing when <see cref="CanApply"/> is false. A response or conflict that arrives after
    /// <see cref="Clear"/> stores nothing: the elements it was for are gone.
    /// </summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply)
        {
            return;
        }

        var inserts = state.NewIds
            .Select(id => state.Elements[id])
            .Select(element => new NodeInsert(element.Id, element.ParentId!.Value, element.Value))
            .ToList();
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
        var request = new ApplyRequest(inserts, edits, deletes);

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
        catch (ApplyRejectedException rejected) when (rejected.StatusCode == HttpStatusCode.Conflict)
        {
            if (applyingIn == state)
            {
                StoreConflicts(rejected.Conflicts);
            }

            throw;
        }
        finally
        {
            applyingIn.IsApplying = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Resolves a conflict in favour of the database: the element takes the database's value and version, and its
    /// pending change is dropped. For a pending delete, the cached descendants deleted with it come back too.
    /// </summary>
    public void TakeDatabase(Guid id)
    {
        var (element, conflict) = ConflictOf(id);
        var loaded = state.Originals[id];
        state.Originals.Remove(id);
        state.ConflictIds.Remove(id);
        state.Elements[id] = DatabaseCopy(loaded, conflict);

        if (element.State == ElementState.Deleted)
        {
            var deletedWithIt = state.Elements.Values
                .Where(member => member.State == ElementState.Deleted && Ancestry.IsDescendantOf(member.Ancestors, id))
                .ToList();
            foreach (var member in deletedWithIt)
            {
                state.Elements[member.Id] = state.Originals[member.Id];
                state.Originals.Remove(member.Id);
            }
        }

        state.ViewTree = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Resolves a conflict in favour of the pending change: it stays, now based on the database's version, so the
    /// next Apply overwrites the other change. The database copy becomes the one <see cref="DiscardAll"/> goes back
    /// to. An edit whose value the database already holds leaves nothing pending.
    /// </summary>
    public void KeepMine(Guid id)
    {
        var (element, conflict) = ConflictOf(id);
        var database = DatabaseCopy(state.Originals[id], conflict);
        state.ConflictIds.Remove(id);

        if (element.State == ElementState.Edited && element.Value == database.Value)
        {
            state.Originals.Remove(id);
            state.Elements[id] = database;
        }
        else
        {
            state.Originals[id] = database;
            state.Elements[id] = element.State == ElementState.Deleted
                ? database with { IsDeleted = true, State = ElementState.Deleted }
                : element with { Version = database.Version, Conflict = null };
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
    /// The applied elements become clean, with the server's final values and versions. Cached descendants of the
    /// deleted ones are deleted too, like the server's cascade.
    /// </summary>
    private void StoreApplied(ApplyResponse response)
    {
        foreach (var id in state.Originals.Keys)
        {
            state.Elements[id] = state.Elements[id] with { State = ElementState.Clean };
        }

        foreach (var id in state.NewIds)
        {
            state.Elements[id] = state.Elements[id] with { State = ElementState.Clean };
        }

        state.Originals.Clear();
        state.NewIds.Clear();

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
            StoreDeletedBelow(node.Id);
        }

        state.ViewTree = null;
    }

    /// <summary>
    /// Stores an Apply's conflicts. Deleted ones first: the element is deleted in the database, so its pending change
    /// can't apply. It becomes deleted with the database's value and version (the loaded ones when it's missing
    /// entirely), and its cached descendants are deleted with it: new ones are removed, the others drop their pending
    /// changes and conflicts. Every other conflicting element that still carries a pending change is flagged.
    /// </summary>
    private void StoreConflicts(IReadOnlyList<NodeConflict> conflicts)
    {
        foreach (var conflict in conflicts.Where(conflict => conflict.Reason == ConflictReason.Deleted))
        {
            if (state.Elements.TryGetValue(conflict.Id, out var element) && element.State != ElementState.New)
            {
                StoreDeletedInDatabase(element, conflict);
            }
        }

        foreach (var conflict in conflicts.Where(conflict => conflict.Reason != ConflictReason.Deleted))
        {
            if (state.Elements.TryGetValue(conflict.Id, out var element)
                && (element.State is ElementState.Edited or ElementState.Deleted)
                && element.Conflict is null)
            {
                state.Elements[conflict.Id] = element with { Conflict = conflict };
                state.ConflictIds.Add(conflict.Id);
            }
        }

        state.ViewTree = null;
    }

    private void StoreDeletedInDatabase(CachedElement element, NodeConflict conflict)
    {
        var loaded = state.Originals.GetValueOrDefault(element.Id, element);
        state.Elements[element.Id] = loaded with
        {
            Value = conflict.Value ?? loaded.Value,
            Version = conflict.Version ?? loaded.Version,
            IsDeleted = true,
            State = ElementState.Clean,
            Conflict = null,
        };
        state.Originals.Remove(element.Id);
        state.ConflictIds.Remove(element.Id);
        StoreDeletedBelow(element.Id);
    }

    /// <summary>The element with the id and its conflict, for resolving it.</summary>
    private (CachedElement Element, NodeConflict Conflict) ConflictOf(Guid id)
    {
        if (IsApplying)
        {
            throw new InvalidOperationException("Conflicts can't be resolved while an Apply is in flight.");
        }

        if (!state.Elements.TryGetValue(id, out var element))
        {
            throw new InvalidOperationException($"The element {id} isn't cached.");
        }

        return element.Conflict is { } conflict
            ? (element, conflict)
            : throw new InvalidOperationException($"The element {id} has no conflict to resolve.");
    }

    /// <summary>The loaded copy with the database's value and version from a version conflict, live and clean.</summary>
    private static CachedElement DatabaseCopy(CachedElement loaded, NodeConflict conflict) => loaded with
    {
        Value = conflict.Value ?? loaded.Value,
        Version = conflict.Version ?? loaded.Version,
        IsDeleted = false,
        State = ElementState.Clean,
        Conflict = null,
    };

    private static void ThrowIfConflicting(CachedElement element)
    {
        if (element.Conflict is not null)
        {
            throw new InvalidOperationException(
                $"The element {element.Id} conflicts with the database; take the database copy or keep yours first.");
        }
    }

    /// <summary>Records a pending delete: the loaded copy is kept, and any pending edit and its conflict are dropped.</summary>
    private void MarkPendingDeleted(CachedElement element)
    {
        var loaded = state.Originals.GetValueOrDefault(element.Id, element);
        state.Originals.TryAdd(element.Id, loaded);
        state.Elements[element.Id] = loaded with { IsDeleted = true, State = ElementState.Deleted };
        state.ConflictIds.Remove(element.Id);
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

    /// <summary>
    /// The database deleted the element's subtree, so every cached descendant is deleted with nothing pending: new
    /// ones are removed, as they can't be inserted there any more; the others go back to their loaded copy, deleted,
    /// and their conflicts are dropped.
    /// </summary>
    private void StoreDeletedBelow(Guid id)
    {
        var descendants = state.Elements.Values
            .Where(element => Ancestry.IsDescendantOf(element.Ancestors, id))
            .ToList();
        foreach (var descendant in descendants)
        {
            if (descendant.State == ElementState.New)
            {
                state.Elements.Remove(descendant.Id);
                state.NewIds.Remove(descendant.Id);
                continue;
            }

            state.Elements[descendant.Id] = state.Originals.GetValueOrDefault(descendant.Id, descendant) with
            {
                IsDeleted = true,
                State = ElementState.Clean,
                Conflict = null,
            };
            state.Originals.Remove(descendant.Id);
            state.ConflictIds.Remove(descendant.Id);
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

        /// <summary>Elements created locally that wait to be inserted, in creation order, so parents come first.</summary>
        public List<Guid> NewIds { get; } = [];

        /// <summary>
        /// The elements whose <see cref="CachedElement.Conflict"/> is set, in the order the server listed them.
        /// Apply waits until it's empty.
        /// </summary>
        public List<Guid> ConflictIds { get; } = [];

        /// <summary>An Apply request is in flight for these elements.</summary>
        public bool IsApplying { get; set; }

        /// <summary>Built from <see cref="Elements"/> on first read; reset to null on every change.</summary>
        public IReadOnlyList<CachedTreeRow>? ViewTree { get; set; }
    }
}
