using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using NewStore.FakePay.Webhooks;

namespace NewStore.FakePay.Sessions;

public sealed record CreateSessionRequest(
    decimal Amount, string? SuccessUrl, string? CancelUrl, DateTimeOffset ExpiresAt, Dictionary<string, string>? Metadata);

public sealed record RefundRequest(string? SessionId);

public static class SessionEndpoints
{
    // Stripe: expires_at "can be anywhere from 30 minutes to 24 hours after Checkout Session creation".
    // A caller computes "now + 30 minutes" and the request needs a moment to arrive, so a few seconds of slack on the lower bound
    // keep that caller from getting a 400 for arriving late.
    private static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder routes, FakePayOptions options)
    {
        var api = routes.MapGroup("/v1").AddEndpointFilter<ApiKeyFilter>();
        api.MapPost("/checkout/sessions", CreateSession);
        api.MapPost("/refunds", CreateRefund);

        routes.MapGet("/checkout/{id}", ShowPage);
        routes.MapPost("/checkout/{id}/pay", PayOnPage);
        routes.MapPost("/checkout/{id}/decline", DeclineOnPage);

        if (options.EnableTestEndpoints)
        {
            routes.MapPost("/test/sessions/{id}/pay", PayWithoutPage);
        }

        return routes;
    }

    private static IResult CreateSession(
        CreateSessionRequest request, HttpContext http, SessionStore store, IOptions<FakePayOptions> options, TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>();
        var now = time.GetUtcNow();
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            errors["Idempotency-Key"] = ["The Idempotency-Key header is required."];
        }

        if (request.Amount <= 0)
        {
            errors["amount"] = ["amount must be positive."];
        }

        if (!Uri.TryCreate(request.SuccessUrl, UriKind.Absolute, out _))
        {
            errors["successUrl"] = ["successUrl must be an absolute URL."];
        }

        if (request.ExpiresAt < now + MinLifetime - Slack || request.ExpiresAt > now + MaxLifetime)
        {
            errors["expiresAt"] = ["expiresAt must be 30 minutes to 24 hours from now."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var session = store.GetOrCreate(key.ToString(), id => new CheckoutSession(
            id,
            request.Amount,
            request.SuccessUrl!,
            request.CancelUrl ?? request.SuccessUrl!,
            request.ExpiresAt,
            request.Metadata ?? []));
        var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
        return Results.Ok(new
        {
            id = session.Id,
            url = $"{baseUrl}/checkout/{session.Id}",
            status = StatusName(session.Status),
            expiresAt = session.ExpiresAt,
        });
    }

    private static IResult CreateRefund(RefundRequest request, HttpContext http, SessionStore store)
    {
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["sessionId and the Idempotency-Key header are required."],
            });
        }

        return store.Refund(key.ToString(), request.SessionId, out var refund) switch
        {
            RefundOutcome.Refunded => Results.Ok(new { id = refund!.Id, sessionId = refund.SessionId, amount = refund.Amount, status = "succeeded" }),
            RefundOutcome.NotCompleted => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Only a completed session can be refunded."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such session."),
        };
    }

    private static IResult ShowPage(string id, SessionStore store, TimeProvider time)
    {
        if (store.Find(id) is not { } session)
        {
            return Results.NotFound();
        }

        return IsPayable(session, time)
            ? Results.Content(CheckoutPage.Render(session), "text/html")
            : Results.Content(CheckoutPage.Closed(session), "text/html", statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult PayOnPage(string id, HttpContext http, SessionStore store, WebhookSender webhooks, TimeProvider time)
    {
        switch (PayAndNotify(id, store, webhooks, time, out var session))
        {
            case PayOutcome.Paid:
                // 303 See Other: the browser follows a POST's answer with a GET of the success page.
                http.Response.Headers.Location = session!.SuccessUrl;
                return Results.StatusCode(StatusCodes.Status303SeeOther);
            case PayOutcome.NotOpen:
                return Results.Content(CheckoutPage.Closed(session!), "text/html", statusCode: StatusCodes.Status409Conflict);
            default:
                return Results.NotFound();
        }
    }

    private static IResult DeclineOnPage(string id, SessionStore store, TimeProvider time)
    {
        if (store.Find(id) is not { } session)
        {
            return Results.NotFound();
        }

        // A decline shows an error and changes nothing: the session stays open, so the buyer can pay.
        return IsPayable(session, time)
            ? Results.Content(CheckoutPage.Render(session, "Your card was declined. Try again."), "text/html")
            : Results.Content(CheckoutPage.Closed(session), "text/html", statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult PayWithoutPage(string id, SessionStore store, WebhookSender webhooks, TimeProvider time) =>
        PayAndNotify(id, store, webhooks, time, out _) switch
        {
            PayOutcome.Paid => Results.NoContent(),
            PayOutcome.NotOpen => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The session is not open."),
            _ => Results.NotFound(),
        };

    private static PayOutcome PayAndNotify(
        string id, SessionStore store, WebhookSender webhooks, TimeProvider time, out CheckoutSession? session)
    {
        var outcome = store.Pay(id, time.GetUtcNow(), out session);
        if (outcome == PayOutcome.Paid)
        {
            webhooks.Enqueue(WebhookEvent.For(WebhookTypes.SessionCompleted, id));
        }

        return outcome;
    }

    private static bool IsPayable(CheckoutSession session, TimeProvider time) =>
        session.Status == SessionStatus.Open && session.ExpiresAt > time.GetUtcNow();

    private static string StatusName(SessionStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Checks <c>Authorization: Bearer &lt;ApiKey&gt;</c> in constant time and answers 401 otherwise.</summary>
    private sealed class ApiKeyFilter(IOptions<FakePayOptions> options) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            var presented = header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : "";
            var expected = options.Value.ApiKey;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid API key.");
            }

            return await next(context);
        }
    }
}
