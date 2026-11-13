using System.Globalization;
using System.Net;

namespace NewStore.FakePay.Sessions;

/// <summary>The hosted checkout page: the amount, a Pay button, a Decline button and, after a decline, an error.</summary>
internal static class CheckoutPage
{
    public static string Render(CheckoutSession session, string? error = null)
    {
        var id = WebUtility.HtmlEncode(session.Id);
        var amount = session.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var errorHtml = error is null
            ? ""
            : $"""<p data-testid="fakepay-error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";
        return $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>FakePay checkout</title></head>
            <body>
              <h1>FakePay checkout</h1>
              <p>Amount due: <strong data-testid="fakepay-amount">{amount}</strong></p>
              {errorHtml}
              <form method="post" action="/checkout/{id}/pay"><button type="submit" data-testid="fakepay-pay">Pay</button></form>
              <form method="post" action="/checkout/{id}/decline"><button type="submit" data-testid="fakepay-decline">Decline</button></form>
            </body>
            </html>
            """;
    }

    public static string Closed(CheckoutSession session) =>
        $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>FakePay checkout</title></head>
        <body><p data-testid="fakepay-closed">This session is {session.Status.ToString().ToLowerInvariant()}.</p></body></html>
        """;
}
