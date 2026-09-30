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
                "ordered by lowercase value, then id. Deleted elements are included.");

        return app;
    }

    private static async Task<Ok<ChildrenPage>> ListChildrenAsync(
        Guid? parentId,
        TreeDbContext db,
        CancellationToken cancellationToken)
    {
        // Reads one row more than a page to know whether more exist; no count query.
        var items = await db.Nodes
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.Value.ToLower())
            .ThenBy(n => n.Id)
            .Select(n => new NodeListItem(
                n.Id,
                n.Value,
                n.IsDeleted,
                db.Nodes.Any(child => child.ParentId == n.Id)))
            .Take(ChildrenPage.PageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = items.Count > ChildrenPage.PageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return TypedResults.Ok(new ChildrenPage(items, hasMore));
    }
}
