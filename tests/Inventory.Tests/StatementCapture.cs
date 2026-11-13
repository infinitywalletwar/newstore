using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NewStore.Inventory.Tests;

/// <summary>One statement EF Core sent, and the trace of the request that sent it (default when no request was running).</summary>
public sealed record CapturedStatement(ActivityTraceId TraceId, string Sql);

/// <summary>
/// Records every statement the app's <see cref="InventoryDbContext"/> sends, so a test counts the statements of one
/// request. Commands come from <see cref="IDbCommandInterceptor"/>; BEGIN, SAVEPOINT, RELEASE SAVEPOINT, COMMIT and ROLLBACK
/// from <see cref="IDbTransactionInterceptor"/>. A transaction disposed without a commit rolls back without an interceptor
/// call, so that ROLLBACK is not recorded. Each statement carries <see cref="Activity.Current"/>'s trace id: EF runs a
/// request's commands inside the request's activity, and every client of <see cref="NewStoreAppFactory{TProgram}"/> sends its
/// own trace id.
/// </summary>
public sealed class StatementCapture : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly ConcurrentQueue<CapturedStatement> _statements = new();

    /// <summary>The statements sent for the requests of <paramref name="client"/>, in order.</summary>
    public IReadOnlyList<string> Of(HttpClient client)
    {
        // traceparent is version-traceid-parentid-flags (NewStoreAppFactory.ConfigureClient).
        var traceId = ActivityTraceId.CreateFromString(client.DefaultRequestHeaders.GetValues("traceparent").Single().Split('-')[1]);
        return [.. _statements.Where(s => s.TraceId == traceId).Select(s => s.Sql)];
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        Record("BEGIN");
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult> CreatingSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Record("SAVEPOINT");
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult> ReleasingSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Record("RELEASE SAVEPOINT");
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Record("COMMIT");
        return ValueTask.FromResult(result);
    }

    public ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Record("ROLLBACK");
        return ValueTask.FromResult(result);
    }

    private void Record(string sql) => _statements.Enqueue(new CapturedStatement(Activity.Current?.TraceId ?? default, sql));
}
