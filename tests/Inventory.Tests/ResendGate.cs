using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Holds back the second reserve statement (the one that starts with <c>WITH</c>) of the app until the test calls
/// <see cref="Release"/>. <see cref="ReserveDeadlockTests"/> uses it so that the resend after a deadlock cannot overtake the
/// test's sweep for the edition row. Unarmed, it lets every statement through.
/// </summary>
public sealed class ResendGate : DbCommandInterceptor
{
    private TaskCompletionSource? _release;
    private int _statements;

    /// <summary>Starts counting reserve statements; the second one waits for <see cref="Release"/>.</summary>
    public void HoldSecondStatement()
    {
        _statements = 0;
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => _release?.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_release is { } release
            && command.CommandText.TrimStart().StartsWith("WITH", StringComparison.Ordinal)
            && Interlocked.Increment(ref _statements) == 2)
        {
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }
}
