using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using TreeEditor.Data;

namespace TreeEditor.Api.Tests.Harness;

/// <summary>
/// A Postgres advisory lock held by the test's own transaction on the test database. Applies that need the lock
/// queue up behind the test until it releases the lock, which makes races between them deterministic.
/// </summary>
public sealed class HeldAdvisoryLock : IAsyncDisposable
{
    /// <summary>How long requests get to queue up for the lock.</summary>
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly AsyncServiceScope scope;
    private readonly TreeDbContext db;
    private readonly IDbContextTransaction transaction;

    private HeldAdvisoryLock(long key, AsyncServiceScope scope, TreeDbContext db, IDbContextTransaction transaction)
    {
        Key = key;
        this.scope = scope;
        this.db = db;
        this.transaction = transaction;
    }

    public long Key { get; }

    public static async Task<HeldAdvisoryLock> TakeAsync(ApiHarness api, long key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);

        var scope = api.CreateDatabaseScope();
        var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        return new HeldAdvisoryLock(key, scope, db, transaction);
    }

    /// <summary>
    /// Waits until every one of <paramref name="requests"/> waits for this lock: as many other sessions wait for it
    /// in <c>pg_locks</c>. Fails as soon as a request finishes instead.
    /// </summary>
    public async Task WaitUntilQueuedAsync(
        IReadOnlyCollection<Task<HttpResponseMessage>> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            foreach (var request in requests.Where(request => request.IsCompleted))
            {
                var response = await request;
                throw new InvalidOperationException(
                    $"A request finished with {(int)response.StatusCode} instead of waiting for advisory lock {Key}.");
            }

            var waiting = await CountWaitingAsync(cancellationToken);
            if (waiting == requests.Count)
            {
                return;
            }

            if (waiting > requests.Count || elapsed.Elapsed > QueueTimeout)
            {
                throw new TimeoutException(
                    $"Expected {requests.Count} session(s) waiting for advisory lock {Key}, found {waiting}.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Commits the test's transaction, which releases the lock to the first request queued for it.</summary>
    public Task ReleaseAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await scope.DisposeAsync();
    }

    /// <summary>
    /// Sessions waiting for this lock in the test database. A bigint key shows in <c>pg_locks</c> as its high half in
    /// <c>classid</c>, its low half in <c>objid</c>, and <c>objsubid</c> 1.
    /// </summary>
    private Task<int> CountWaitingAsync(CancellationToken cancellationToken) =>
        db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND NOT granted
                  AND objsubid = 1
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND ((classid::bigint << 32) | objid::bigint) = {Key}
                """)
            .SingleAsync(cancellationToken);
}
