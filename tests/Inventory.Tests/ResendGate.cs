using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Holds the next reserve statement (the one that starts with <c>WITH</c>) the app sends after <see cref="HoldNextReserveStatement"/>
/// until the returned scope is disposed. <see cref="ReserveDeadlockTests"/> uses it so that the resend after a deadlock cannot
/// overtake the test's sweep for the edition row. Unarmed, it lets every statement through (one null check).
/// </summary>
public sealed class ResendGate : DbCommandInterceptor
{
    private TaskCompletionSource? _armed;
    private int _held;

    /// <summary>How many reserve statements waited at the gate. A test asserts it, so a gate that never holds fails the test.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>The next reserve statement waits until the returned scope is disposed. Disposing also disarms the gate.</summary>
    public IDisposable HoldNextReserveStatement()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _armed, gate);
        return new Opener(() =>
        {
            Interlocked.CompareExchange(ref _armed, null, gate);
            gate.TrySetResult();
        });
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // Exchange takes the gate atomically: exactly one statement waits, whatever the thread.
        if (command.CommandText.TrimStart().StartsWith("WITH", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _armed, null) is { } gate)
        {
            Interlocked.Increment(ref _held);
            await gate.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private sealed class Opener(Action open) : IDisposable
    {
        public void Dispose() => open();
    }
}
