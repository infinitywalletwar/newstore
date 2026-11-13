using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Payments.Payments;

public static class PaymentEndpoints
{
    /// <summary>The largest amount <c>numeric(10,2)</c> holds: eight digits before the point, two after.</summary>
    private const decimal MaxAmount = 99_999_999.99m;

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
        else if (Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero) > MaxAmount)
        {
            // The column is numeric(10,2). A larger value would reach PostgreSQL and come back as a 500 (numeric field overflow).
            // The rounding is the database's: a value that rounds up past the maximum overflows too.
            errors["amount"] = [$"amount must not be more than {MaxAmount}."];
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
