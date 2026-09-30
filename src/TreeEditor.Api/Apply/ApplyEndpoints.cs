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
/// deletes in that order and <c>COMMIT</c>. Only edits are supported so far.
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
                "Applies inserts, edits and deletes in one transaction; only edits are supported so far. " +
                "An edit applies when its version still matches and the element isn't deleted. Values are trimmed; " +
                "a value colliding with a live sibling (case-insensitive) gets the first free \" (n)\" suffix. " +
                "Returns every changed element's final value, new version and deleted flag. An invalid value or an " +
                "id used twice is a 400 problem; changes made elsewhere are a 409 problem listing every conflicting " +
                $"element under \"{ApplyProblem.ConflictsMember}\". Nothing is written on an error.");

        return app;
    }

    private static async Task<Results<Ok<ApplyResponse>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        ApplyRequest request,
        TreeDbContext db,
        ReadCache readCache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, out var edits);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors, title: "The changes are invalid; nothing was applied.");
        }

        // An element's root never changes, so the touched roots can be read before the transaction.
        var roots = await TouchedRootsAsync(db, edits, cancellationToken);
        loggerFactory.CreateLogger(typeof(ApplyEndpoints))
            .LogInformation("Applying {EditCount} edits in {RootCount} root trees", edits.Count, roots.Count);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Advisory locks, one per touched root in sorted order, belong here (ticket #10).

        var editIds = edits.Select(edit => edit.Id).ToArray();
        var nodes = await db.Nodes
            .Where(n => editIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, cancellationToken);

        var conflicts = edits
            .Select(edit => EditConflict(edit, nodes.GetValueOrDefault(edit.Id)))
            .OfType<NodeConflict>()
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

        var applied = new List<AppliedNode>(edits.Count);
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
    /// Request-level rules, checked before touching the database: valid values and each id at most once.
    /// On success, <paramref name="edits"/> carry normalised values.
    /// </summary>
    private static Dictionary<string, string[]> Validate(ApplyRequest request, out List<NodeEdit> edits)
    {
        var errors = new Dictionary<string, string[]>();
        var inserts = request.Inserts ?? [];
        var deletes = request.Deletes ?? [];
        edits = [];

        // Inserts come with ticket #7 and deletes with #8; until then they are refused rather than ignored.
        if (inserts.Count > 0)
        {
            errors["inserts"] = ["Inserts are not supported yet."];
        }

        if (deletes.Count > 0)
        {
            errors["deletes"] = ["Deletes are not supported yet."];
        }

        var ids = new HashSet<Guid>(inserts.Where(insert => insert is not null).Select(insert => insert.Id));
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

    private static async Task<List<Guid>> TouchedRootsAsync(
        TreeDbContext db,
        List<NodeEdit> edits,
        CancellationToken cancellationToken)
    {
        var ids = edits.Select(edit => edit.Id).ToArray();
        var ancestors = await db.Nodes
            .Where(n => ids.Contains(n.Id))
            .Select(n => n.Ancestors)
            .ToListAsync(cancellationToken);
        return [.. ancestors.Select(Ancestry.RootOf).Distinct().Order()];
    }

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
