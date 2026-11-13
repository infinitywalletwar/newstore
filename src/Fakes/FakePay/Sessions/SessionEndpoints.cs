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
    // Stripe: expires_at "can be anywhere from 30 minutes to 24 hours after Checkout Session creation". Exact, as Stripe's range is:
    // a caller that computes "now + 30 minutes" must add a margin of its own (Payments does: ProviderOptions.ExpiryMargin).
    private static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder routes, FakePayOptions options)
    {
        var api = routes.MapGroup("/v1");
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

        if (request.ExpiresAt < now + MinLifetime || request.ExpiresAt > now + MaxLifetime)
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

    /// <summary>
    /// Checks <c>Authorization: Bearer &lt;ApiKey&gt;</c> on every <c>/v1</c> request, in constant time, and answers 401 otherwise. It is
    /// middleware and not an endpoint filter, because a filter runs after the body is bound: a request with a missing key and a
    /// missing body would get a 400 or 500 instead of the 401.
    /// </summary>
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/v1"),
            branch => branch.Use(async (context, next) =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                const string prefix = "Bearer ";
                var presented = header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : "";
                var expected = context.RequestServices.GetRequiredService<IOptions<FakePayOptions>>().Value.ApiKey;
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await context.Response.WriteAsJsonAsync(
                        new { title = "Invalid API key.", status = StatusCodes.Status401Unauthorized }, context.RequestAborted);
                    return;
                }

                await next(context);
            }));
}
