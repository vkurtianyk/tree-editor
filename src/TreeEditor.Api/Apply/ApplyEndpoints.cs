using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TreeEditor.Api.Caching;
using TreeEditor.Contracts;
using TreeEditor.Data;
using TreeEditor.Domain;

namespace TreeEditor.Api.Apply;

/// <summary>
/// Apply: every pending change of a local cache in one all-or-nothing transaction.
/// Steps: validate the request (400), derive the touched roots from the stored ancestors, <c>BEGIN</c>,
/// check every change against the database and collect the conflicts (409), then write inserts, edits and
/// deletes in that order and <c>COMMIT</c>. Deletes are not supported yet.
/// </summary>
public static class ApplyEndpoints
{
    /// <summary>Candidate values checked against the siblings per query when resolving a suffix.</summary>
    private const int CandidateBatchSize = 16;

    public static IEndpointRouteBuilder MapApplyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.Apply, ApplyAsync)
            .WithTags("Apply")
            .WithName("Apply")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Apply changes")
            .WithDescription(
                "Applies inserts, edits and deletes in one transaction; deletes are not supported yet. " +
                "An insert applies when its parent exists and isn't deleted; the parent may be another insert of the " +
                "same request, and the ancestors are computed from the parent. An edit applies when its version still " +
                "matches and the element isn't deleted. Values are trimmed; a value colliding with a live sibling " +
                "(case-insensitive) gets the first free \" (n)\" suffix. Returns every changed element's final value, " +
                "new version and deleted flag. An invalid value, an id used twice or an insert id that already exists " +
                "is a 400 problem; changes made elsewhere are a 409 problem listing every conflicting element under " +
                $"\"{ApplyProblem.ConflictsMember}\". Nothing is written on an error.");

        return app;
    }

    private static async Task<Results<Ok<ApplyResponse>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        ApplyRequest request,
        TreeDbContext db,
        ReadCache readCache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, out var inserts, out var edits);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors, title: "The changes are invalid; nothing was applied.");
        }

        // Inserts under other inserts of the request hang, through them, off a stored parent.
        var insertIds = inserts.Select(insert => insert.Id).ToArray();
        var storedParentIds = inserts
            .Select(insert => insert.ParentId)
            .Where(parentId => !insertIds.Contains(parentId))
            .Distinct()
            .ToArray();

        // An element's root never changes, so the touched roots can be read before the transaction.
        var roots = await TouchedRootsAsync(db, [.. storedParentIds, .. edits.Select(edit => edit.Id)], cancellationToken);
        loggerFactory.CreateLogger(typeof(ApplyEndpoints)).LogInformation(
            "Applying {InsertCount} inserts and {EditCount} edits in {RootCount} root trees",
            inserts.Count,
            edits.Count,
            roots.Count);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Advisory locks, one per touched root in sorted order, belong here (ticket #10).

        var existingIds = await db.Nodes
            .Where(n => insertIds.Contains(n.Id))
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);
        if (existingIds.Count > 0)
        {
            // Ids are final at creation, so reusing one (even a deleted element's) is a client error, not a conflict.
            var idErrors = request.Inserts.Index()
                .Where(entry => existingIds.Contains(entry.Item.Id))
                .ToDictionary(
                    entry => $"inserts[{entry.Index}].id",
                    entry => new[] { $"An element with the id {entry.Item.Id} already exists." });
            return TypedResults.ValidationProblem(idErrors, title: "The changes are invalid; nothing was applied.");
        }

        var parents = await db.Nodes
            .Where(n => storedParentIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, cancellationToken);

        var editIds = edits.Select(edit => edit.Id).ToArray();
        var nodes = await db.Nodes
            .Where(n => editIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, cancellationToken);

        var conflicts = storedParentIds
            .Select(parentId => ParentConflict(parentId, parents.GetValueOrDefault(parentId)))
            .Concat(edits.Select(edit => EditConflict(edit, nodes.GetValueOrDefault(edit.Id))))
            .OfType<NodeConflict>()
            // A deleted parent that is also edited is listed once.
            .Distinct()
            .ToList();
        if (conflicts.Count > 0)
        {
            // Nothing was written; disposing the transaction rolls it back.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The changes conflict with the database; nothing was applied.",
                detail: $"{conflicts.Count} element(s) changed in the database after they were loaded.",
                extensions: new Dictionary<string, object?> { [ApplyProblem.ConflictsMember] = conflicts });
        }

        var applied = new List<AppliedNode>(inserts.Count + edits.Count);
        var inserted = new Dictionary<Guid, Node>(inserts.Count);
        foreach (var insert in inserts)
        {
            // Inserts are ordered parents first, so a parent inserted by this request is already written.
            var parent = inserted.GetValueOrDefault(insert.ParentId) ?? parents[insert.ParentId];
            var node = new Node
            {
                Id = insert.Id,
                ParentId = parent.Id,
                // The position comes from the stored parent alone; nothing else the client sends is trusted.
                Ancestors = Ancestry.ForChild(parent.Ancestors, insert.Id),
            };
            node.Value = await ResolveSiblingValueAsync(db, node, insert.Value, cancellationToken);
            db.Nodes.Add(node);
            // One insert at a time, so the next suffix sees this value among its siblings.
            await db.SaveChangesAsync(cancellationToken);
            inserted.Add(node.Id, node);
            applied.Add(new AppliedNode(node.Id, node.Value, node.Version, node.IsDeleted));
        }

        foreach (var edit in edits)
        {
            var node = nodes[edit.Id];
            node.Value = await ResolveSiblingValueAsync(db, node, edit.Value, cancellationToken);
            // One edit at a time, so the next edit's suffix sees this value among its siblings.
            // The version (xmin) is the concurrency token: the update matches only the checked version.
            await db.SaveChangesAsync(cancellationToken);
            applied.Add(new AppliedNode(node.Id, node.Value, node.Version, node.IsDeleted));
        }

        await transaction.CommitAsync(cancellationToken);
        // Only after the commit: invalidating earlier would let a read store the old data again.
        await readCache.InvalidateAsync(roots, applied.Select(node => node.Id));
        return TypedResults.Ok(new ApplyResponse(applied));
    }

    /// <summary>
    /// Request-level rules, checked before touching the database: valid values, each id at most once, and new
    /// parents that lead to a stored element. On success, <paramref name="inserts"/> are ordered parents first and
    /// both lists carry normalised values.
    /// </summary>
    private static Dictionary<string, string[]> Validate(
        ApplyRequest request,
        out List<NodeInsert> inserts,
        out List<NodeEdit> edits)
    {
        var errors = new Dictionary<string, string[]>();
        var deletes = request.Deletes ?? [];
        edits = [];

        // Deletes come with ticket #8; until then they are refused rather than ignored.
        if (deletes.Count > 0)
        {
            errors["deletes"] = ["Deletes are not supported yet."];
        }

        var ids = new HashSet<Guid>();
        inserts = ValidateInserts(request.Inserts ?? [], ids, errors);
        var requestEdits = request.Edits ?? [];
        for (var i = 0; i < requestEdits.Count; i++)
        {
            var edit = requestEdits[i];
            if (edit is null)
            {
                errors[$"edits[{i}]"] = ["An edit is required."];
                continue;
            }

            if (!ids.Add(edit.Id))
            {
                errors[$"edits[{i}].id"] = [$"The id {edit.Id} appears more than once in the request."];
            }

            if (ElementValue.TryNormalize(edit.Value, out var value, out var error))
            {
                edits.Add(edit with { Value = value });
            }
            else
            {
                errors[$"edits[{i}].value"] = [error];
            }
        }

        return errors;
    }

    /// <summary>
    /// Checks every insert, adding its id to <paramref name="ids"/>, and returns the valid ones parents first
    /// (request order otherwise). An insert whose new parents loop back instead of reaching a stored element is invalid.
    /// </summary>
    private static List<NodeInsert> ValidateInserts(
        IReadOnlyList<NodeInsert> requestInserts,
        HashSet<Guid> ids,
        Dictionary<string, string[]> errors)
    {
        var valid = new List<(int Index, NodeInsert Insert)>();
        for (var i = 0; i < requestInserts.Count; i++)
        {
            var insert = requestInserts[i];
            if (insert is null)
            {
                errors[$"inserts[{i}]"] = ["An insert is required."];
                continue;
            }

            var isValid = true;
            if (insert.Id == Guid.Empty)
            {
                errors[$"inserts[{i}].id"] = ["An id is required."];
                isValid = false;
            }
            else if (!ids.Add(insert.Id))
            {
                errors[$"inserts[{i}].id"] = [$"The id {insert.Id} appears more than once in the request."];
                isValid = false;
            }

            if (ElementValue.TryNormalize(insert.Value, out var value, out var error))
            {
                insert = insert with { Value = value };
            }
            else
            {
                errors[$"inserts[{i}].value"] = [error];
                isValid = false;
            }

            if (isValid)
            {
                valid.Add((i, insert));
            }
        }

        // Breadth first from the inserts under stored parents; valid ids are unique, so each insert is queued once.
        var validIds = valid.Select(entry => entry.Insert.Id).ToHashSet();
        var children = valid.ToLookup(entry => entry.Insert.ParentId);
        var queue = new Queue<(int Index, NodeInsert Insert)>(
            valid.Where(entry => !validIds.Contains(entry.Insert.ParentId)));
        var ordered = new List<NodeInsert>(valid.Count);
        var reached = new HashSet<int>();
        while (queue.TryDequeue(out var entry))
        {
            ordered.Add(entry.Insert);
            reached.Add(entry.Index);
            foreach (var child in children[entry.Insert.Id])
            {
                queue.Enqueue(child);
            }
        }

        foreach (var (index, insert) in valid.Where(entry => !reached.Contains(entry.Index)))
        {
            errors[$"inserts[{index}].parentId"] =
                [$"The parent {insert.ParentId} leads back to this insert instead of to an existing element."];
        }

        return ordered;
    }

    private static async Task<List<Guid>> TouchedRootsAsync(
        TreeDbContext db,
        Guid[] ids,
        CancellationToken cancellationToken)
    {
        var ancestors = await db.Nodes
            .Where(n => ids.Contains(n.Id))
            .Select(n => n.Ancestors)
            .ToListAsync(cancellationToken);
        return [.. ancestors.Select(Ancestry.RootOf).Distinct().Order()];
    }

    /// <summary>
    /// Why inserts under a stored parent can't be applied, with the database's current state; null when they can.
    /// </summary>
    private static NodeConflict? ParentConflict(Guid parentId, Node? parent) => parent switch
    {
        // A changed parent value is fine; only an element that is gone can't take children.
        null => new NodeConflict(parentId, ConflictReason.Deleted, Value: null, Version: null, IsDeleted: true),
        { IsDeleted: true } =>
            new NodeConflict(parent.Id, ConflictReason.Deleted, parent.Value, parent.Version, IsDeleted: true),
        _ => null,
    };

    /// <summary>Why the edit can't be applied, with the database's current state; null when it can.</summary>
    private static NodeConflict? EditConflict(NodeEdit edit, Node? node) => node switch
    {
        // No longer exists at all: treated as deleted in the database.
        null => new NodeConflict(edit.Id, ConflictReason.Deleted, Value: null, Version: null, IsDeleted: true),
        { IsDeleted: true } => new NodeConflict(node.Id, ConflictReason.Deleted, node.Value, node.Version, IsDeleted: true),
        _ when node.Version != edit.Version =>
            new NodeConflict(node.Id, ConflictReason.VersionChanged, node.Value, node.Version, IsDeleted: false),
        _ => null,
    };

    /// <summary>
    /// The first suffix candidate no live sibling of <paramref name="node"/> holds (roots are siblings of each other),
    /// the element itself excluded. Candidates are checked a batch per query on the (parent_id, lower(value)) index.
    /// </summary>
    private static async Task<string> ResolveSiblingValueAsync(
        TreeDbContext db,
        Node node,
        string value,
        CancellationToken cancellationToken)
    {
        var parentId = node.ParentId;
        var id = node.Id;
        foreach (var candidates in SiblingSuffix.Candidates(value).Chunk(CandidateBatchSize))
        {
            var keys = candidates.Select(ElementValue.SiblingKey).ToArray();
            var taken = await db.Nodes
                .Where(n => n.ParentId == parentId && !n.IsDeleted && n.Id != id && keys.Contains(n.Value.ToLower()))
                .Select(n => n.Value.ToLower())
                .ToListAsync(cancellationToken);

            var free = candidates.FirstOrDefault(candidate => !taken.Contains(ElementValue.SiblingKey(candidate)));
            if (free is not null)
            {
                return free;
            }
        }

        throw new UnreachableException("Suffix candidates never run out.");
    }
}
