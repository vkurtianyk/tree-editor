using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TreeEditor.Contracts;
using TreeEditor.Data;

namespace TreeEditor.Api.Reset;

public static class ResetEndpoints
{
    // Refilling up to 1M elements outlasts the 30 s default command timeout.
    private static readonly TimeSpan ResetCommandTimeout = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapResetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.Reset, ResetAsync)
            .WithTags("Reset")
            .WithName("Reset")
            .WithSummary("Reset")
            .WithDescription(
                "Restores the sample data: empties nodes and copies seed_nodes into it in one transaction, " +
                "so every element gets back its seed id, value and deleted flag (with a new version). " +
                "Waits for running Applies; Applies that arrive meanwhile wait for it.");

        return app;
    }

    private static async Task<NoContent> ResetAsync(TreeDbContext db, CancellationToken cancellationToken)
    {
        db.Database.SetCommandTimeout(ResetCommandTimeout);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // TRUNCATE's ACCESS EXCLUSIVE lock is the only coordination with Applies: it waits for the running ones,
        // and new ones wait until the commit, so neither ever sees the other half-done.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE nodes", cancellationToken);
        await db.Database.FillNodesFromSeedAsync(cancellationToken);

        // Fresh statistics, as after the first fill: TRUNCATE resets the table's size estimates.
        await db.Database.ExecuteSqlRawAsync("ANALYZE nodes", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
