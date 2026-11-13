using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Payments.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder routes)
    {
        // Only Orders asks for payments. The gateway never routes here, and Inventory's token is refused by the policy.
        var payments = routes.MapGroup("/payments").RequireAuthorization(Policies.Client("orders"));
        payments.MapPost("/", CreateAsync);
        payments.MapGet("/{id:guid}", GetAsync);
        return routes;
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request, PaymentService payments, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.OrderId == Guid.Empty)
        {
            errors["orderId"] = ["orderId is required."];
        }

        if (request.Amount <= 0)
        {
            errors["amount"] = ["amount must be positive."];
        }

        if (!Uri.TryCreate(request.ReturnUrl, UriKind.Absolute, out var returnUrl) || returnUrl.Scheme is not ("http" or "https"))
        {
            errors["returnUrl"] = ["returnUrl must be an absolute http or https URL."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        try
        {
            var (attempt, created) = await payments.CreateAsync(request.OrderId, request.Amount, returnUrl!.AbsoluteUri, ct);
            var response = PaymentResponse.From(attempt);
            return created ? Results.Created($"/payments/{attempt.Id}", response) : Results.Ok(response);
        }
        catch (Exception ex) when (TransientFailures.IsTransient(ex))
        {
            // The provider is down. A repeat of this call is safe: the attempt exists and its idempotency key is fixed.
            return Problems.DependencyUnavailable(http.Response);
        }
    }

    private static async Task<IResult> GetAsync(Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var attempt = await db.PaymentAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return attempt is null ? Problems.NotFound("Payment") : Results.Ok(PaymentResponse.From(attempt));
    }
}
