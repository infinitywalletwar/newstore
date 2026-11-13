using System.Net;
using System.Net.Http.Json;

using NewStore.Messaging;

using Polly;

namespace NewStore.Orders.Clients;

/// <summary>
/// How Orders' clients send a request. Every way a call can fail to get an answer ends as <see cref="TransientFailureException"/>:
/// a 5xx, a 408 or 429 (the statuses the standard handler's retry treats as transient), a connection error, and every exception
/// Polly's pipeline throws when it gives up: <see cref="ExecutionRejectedException"/> is the base of <c>BrokenCircuitException</c>
/// (the circuit is open), <c>TimeoutRejectedException</c> (a timeout fired) and <c>RateLimiterRejectedException</c> (the concurrency
/// limiter is full). Messaging does not know Polly's types, so a handler that let them through would be dead-lettered instead of
/// retried (ADR 0014, point 8).
/// </summary>
internal static class ServiceCall
{
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object? body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = body is null ? null : JsonContent.Create(body),
            };
            response = await http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is ExecutionRejectedException or HttpRequestException)
        {
            throw new TransientFailureException($"POST {path} got no answer: {e.Message}", e);
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new TransientFailureException($"POST {path} answered {status}.");
        }

        return response;
    }

    /// <summary>The <c>type</c> and <c>title</c> of a problem+json reply. A reply that is not one gets a generic pair.</summary>
    public static async Task<(string Type, string Title)> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(ct);
            return (problem?.Type ?? "about:blank", problem?.Title ?? response.ReasonPhrase ?? "Rejected.");
        }
        catch (System.Text.Json.JsonException)
        {
            return ("about:blank", response.ReasonPhrase ?? "Rejected.");
        }
    }

    private sealed record ProblemBody(string? Type, string? Title);
}
