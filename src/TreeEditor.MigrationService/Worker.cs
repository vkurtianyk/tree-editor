using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TreeEditor.Data;
using TreeEditor.MigrationService.Seeding;

namespace TreeEditor.MigrationService;

/// <summary>
/// Applies migrations, (re)generates the sample tree into <c>seed_nodes</c> on first start and whenever the
/// configured size changes, fills <c>nodes</c> from it when it was regenerated or <c>nodes</c> is empty,
/// then stops the host. The AppHost starts the Api only after this completes.
/// </summary>
public sealed class Worker(
    IServiceProvider services,
    IOptions<SeedOptions> seedOptions,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    public const string ActivitySourceName = "TreeEditor.MigrationService";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly TimeSpan SeedCommandTimeout = TimeSpan.FromMinutes(10);

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
        var size = seedOptions.Value.Size;
        var started = Stopwatch.GetTimestamp();

        // Bulk writes of up to 1M elements outlast the 30 s default command timeout.
        db.Database.SetCommandTimeout(SeedCommandTimeout);

        // Aspire enables EF retries; a user transaction must run inside the execution strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The generator returns exactly `size` elements, so any other count means first start or a changed size.
            var regenerate = await db.SeedNodes.CountAsync(cancellationToken) != size;
            if (regenerate)
            {
                await WriteSeedAsync(db, size, cancellationToken);
            }

            if (regenerate || !await db.Nodes.AnyAsync(cancellationToken))
            {
                await FillNodesAsync(db, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });

        logger.LogInformation("Seeding finished in {ElapsedMs} ms", ElapsedMs(started));
    }

    private async Task WriteSeedAsync(TreeDbContext db, int size, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var rows = SampleTree.Generate(size);
        logger.LogInformation("Generated {Count} sample elements in {ElapsedMs} ms", rows.Count, ElapsedMs(started));

        started = Stopwatch.GetTimestamp();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE seed_nodes", cancellationToken);
        var written = await SeedNodeWriter.CopyAsync(db, rows, cancellationToken);
        logger.LogInformation("Wrote {Count} sample elements to seed_nodes in {ElapsedMs} ms", written, ElapsedMs(started));
    }

    private async Task FillNodesAsync(TreeDbContext db, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // After a regeneration, elements left in nodes came from the previous seed.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE nodes", cancellationToken);
        var filled = await db.Database.FillNodesFromSeedAsync(cancellationToken);

        // Fresh statistics, so the first queries after a bulk load get good plans.
        await db.Database.ExecuteSqlRawAsync("ANALYZE nodes", cancellationToken);
        logger.LogInformation("Filled nodes with {Count} elements from seed_nodes in {ElapsedMs} ms", filled, ElapsedMs(started));
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
