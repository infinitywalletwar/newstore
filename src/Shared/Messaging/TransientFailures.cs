using System.Data.Common;

namespace NewStore.Messaging;

/// <summary>Tells a failure that may pass ("not now") from one that never will ("never").</summary>
public static class TransientFailures
{
    /// <summary>
    /// True when <paramref name="e"/> or any inner exception is a <see cref="TransientFailureException"/>,
    /// an <see cref="HttpRequestException"/>, a <see cref="TimeoutException"/> or a <see cref="DbException"/>
    /// whose <see cref="DbException.IsTransient"/> is true.
    /// </summary>
    public static bool IsTransient(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current is TransientFailureException or HttpRequestException or TimeoutException or DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
