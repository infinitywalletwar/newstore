namespace NewStore.Payments.Payments;

public sealed record CreatePaymentRequest(Guid OrderId, decimal Amount, string? ReturnUrl);

public sealed record PaymentResponse(
    Guid Id, Guid OrderId, decimal Amount, string Status, string? CheckoutUrl, DateTimeOffset? SessionExpiresAt)
{
    public static PaymentResponse From(PaymentAttempt attempt) =>
        new(attempt.Id, attempt.OrderId, attempt.Amount, attempt.Status.ToString(), attempt.CheckoutUrl, attempt.SessionExpiresAt);
}
