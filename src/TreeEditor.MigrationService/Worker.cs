using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TreeEditor.Data;
using TreeEditor.MigrationService.Seeding;

namespace TreeEditor.MigrationService;

/// <summary>
/// Applies migrations, writes the sample tree to <c>seed_nodes</c> when it is empty,
/// fills <c>nodes</c> from it when <c>nodes</c> is empty, then stops the host.
/// The AppHost starts the Api only after this completes.
/// </summary>
public sealed class Worker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    public const string ActivitySourceName = "TreeEditor.MigrationService";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrate and seed database", ActivityKind.Client);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();

            await db.Database.MigrateAsync(stoppingToken);
            await SeedAsync(db, stoppingToken);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            logger.LogError(ex, "Database migration or seeding failed");
            Environment.ExitCode = 1; // so the AppHost sees a failed run, not a completed one
            throw;
        }

        lifetime.StopApplication();
    }

    private async Task SeedAsync(TreeDbContext db, CancellationToken cancellationToken)
    {
        // Aspire enables EF retries; a user transaction must run inside the execution strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (!await db.SeedNodes.AnyAsync(cancellationToken))
            {
                var rows = SampleTree.Generate(SampleTree.DefaultSize);
                await SeedNodeWriter.CopyAsync(db, rows, cancellationToken);
                logger.LogInformation("Wrote {Count} sample elements to seed_nodes", rows.Count);
            }

            if (!await db.Nodes.AnyAsync(cancellationToken))
            {
                var filled = await db.Database.FillNodesFromSeedAsync(cancellationToken);
                logger.LogInformation("Filled nodes with {Count} elements from seed_nodes", filled);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
