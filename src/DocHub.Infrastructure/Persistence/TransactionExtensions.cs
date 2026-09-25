using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Infrastructure.Persistence;

public static class TransactionExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> in a transaction inside the context's (retrying) execution strategy, so a transient
    /// failure retries the whole unit. The change tracker is cleared before each attempt.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(this DocHubDbContext db, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(action);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            var result = await action().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        });
    }

    /// <summary>Exclusive, transaction-owned application lock (serializes lifecycle changes of one document).</summary>
    public static async Task LockAsync(this DocHubDbContext db, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DECLARE @result INT;
            EXEC @result = sys.sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @result < 0 THROW 50042, N'The item is being changed by another request; try again.', 1;
            """, cancellationToken).ConfigureAwait(false);
    }
}
