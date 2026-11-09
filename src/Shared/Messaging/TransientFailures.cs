using System.Data.Common;
using System.Net;

namespace NewStore.Messaging;

/// <summary>Tells a failure that may pass ("not now") from one that never will ("never").</summary>
public static class TransientFailures
{
    /// <summary>
    /// True when <paramref name="e"/> or an exception inside it is transient: a <see cref="TransientFailureException"/>,
    /// a <see cref="TimeoutException"/>, a <see cref="DbException"/> whose <see cref="DbException.IsTransient"/> is true, or an
    /// <see cref="HttpRequestException"/> with no status code, 408, 429 or 500 and above. Any other status (a 404, a 409) is
    /// permanent: <c>EnsureSuccessStatusCode</c> throws <see cref="HttpRequestException"/> for every non-2xx response, and
    /// retrying a 404 would block the partition for good. An <see cref="AggregateException"/> is transient only when
    /// every exception inside it is.
    /// </summary>
    public static bool IsTransient(Exception e) => e switch
    {
        TransientFailureException or TimeoutException => true,
        DbException { IsTransient: true } => true,
        HttpRequestException http when http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int?)http.StatusCode >= 500 => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient),
        _ => e.InnerException is not null && IsTransient(e.InnerException),
    };
}
