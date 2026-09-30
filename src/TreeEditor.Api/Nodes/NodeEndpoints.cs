using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Nodes;

public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Children, ListChildrenAsync)
            .WithTags("Nodes")
            .WithName("ListChildren")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("List children")
            .WithDescription(
                $"Up to {ChildrenPage.PageSize} children of the parent (roots when parentId is absent), " +
                "ordered by lowercase value, then id. Deleted elements are included. " +
                "For the next page pass the previous page's next cursor as afterValue and afterId.");

        app.MapGet(ApiRoutes.Node, LoadNodeAsync)
            .WithTags("Nodes")
            .WithName("LoadNode")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Load node")
            .WithDescription(
                "One element by id: parent id, ancestor ids (root first, ending with the element itself), value, " +
                "version and deleted flag. Deleted elements load too. An unknown id is a 404 problem.");

        return app;
    }

    private static async Task<Results<Ok<NodeDetails>, ProblemHttpResult>> LoadNodeAsync(
        Guid id,
        TreeDbContext db,
        CancellationToken cancellationToken)
    {
        var node = await db.Nodes
            .Where(n => n.Id == id)
            .Select(n => new NodeDetails(n.Id, n.ParentId, n.Ancestors, n.Value, n.Version, n.IsDeleted))
            .SingleOrDefaultAsync(cancellationToken);

        return node is null
            ? TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"No element has the id {id}.")
            : TypedResults.Ok(node);
    }

    private static async Task<Results<Ok<ChildrenPage>, ValidationProblem>> ListChildrenAsync(
        Guid? parentId,
        string? afterValue,
        Guid? afterId,
        TreeDbContext db,
        CancellationToken cancellationToken)
    {
        if ((afterValue is null) != (afterId is null))
        {
            var missing = afterValue is null ? nameof(afterValue) : nameof(afterId);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [missing] = ["afterValue and afterId form one cursor: pass both or neither."],
            });
        }

        var children = db.Nodes.Where(n => n.ParentId == parentId);
        if (afterValue is not null && afterId is { } lastId)
        {
            // Keyset: (lower(value), id) > (@afterValue, @afterId), served by the (parent_id, lower(value), id) index.
            children = children.Where(n => EF.Functions.GreaterThan(
                ValueTuple.Create(n.Value.ToLower(), n.Id),
                ValueTuple.Create(afterValue, lastId)));
        }

        // Reads one row more than a page to know whether more exist; no count query.
        var rows = await children
            .OrderBy(n => n.Value.ToLower())
            .ThenBy(n => n.Id)
            .Select(n => new
            {
                Item = new NodeListItem(
                    n.Id,
                    n.Value,
                    n.IsDeleted,
                    db.Nodes.Any(child => child.ParentId == n.Id)),
                LowerValue = n.Value.ToLower(),
            })
            .Take(ChildrenPage.PageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > ChildrenPage.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // The cursor carries the database's lowercase value, so the next page compares like with like.
        var next = hasMore ? new ChildrenCursor(rows[^1].LowerValue, rows[^1].Item.Id) : null;
        return TypedResults.Ok(new ChildrenPage([.. rows.Select(row => row.Item)], hasMore, next));
    }
}
