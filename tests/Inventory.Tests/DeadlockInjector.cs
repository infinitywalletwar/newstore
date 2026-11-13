using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Makes the next reserve statement (the one that starts with <c>WITH</c>) fail with SQLSTATE 40P01, as PostgreSQL does when
/// it aborts a transaction to break a deadlock. The statement never reaches the database, which is what a rolled-back
/// autocommit statement looks like to the caller. Registered after <see cref="StatementCapture"/>, so the capture still records it.
/// </summary>
public sealed class DeadlockInjector : DbCommandInterceptor
{
    private int _failures;

    /// <summary>Fails the next <paramref name="count"/> reserve statements.</summary>
    public void FailNext(int count) => Interlocked.Exchange(ref _failures, count);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        MaybeFail(command);
        return result;
    }

    private void MaybeFail(DbCommand command)
    {
        if (command.CommandText.TrimStart().StartsWith("WITH", StringComparison.Ordinal) && Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new PostgresException(
                "deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
        }
    }
}
