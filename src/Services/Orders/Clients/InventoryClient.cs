using System.Net;
using System.Net.Http.Json;

namespace NewStore.Orders.Clients;

/// <summary>Orders' copy of the fields of Inventory's reservation that it uses. <c>Status</c> is Inventory's: Pending, Held, Confirmed, Expired or Released.</summary>
public sealed record ReservationInfo(Guid Id, Guid VideoId, string Format, decimal Price, string Status, DateTimeOffset ExpiresAt);

public abstract record ReserveResult
{
    private ReserveResult()
    {
    }

    /// <summary>A reservation Orders may keep: a first reserve (201), or the repeat of one whose 201 was lost (200 <c>Pending</c>).</summary>
    public sealed record Reserved(ReservationInfo Reservation) : ReserveResult;

    /// <summary>Inventory said no, and the buyer gets its answer unchanged: 404 or 409 with Inventory's problem type.</summary>
    public sealed record Rejected(int StatusCode, string Type, string Title) : ReserveResult;
}

public abstract record HoldResult
{
    private HoldResult()
    {
    }

    public sealed record Held(DateTimeOffset ExpiresAt) : HoldResult;

    /// <summary>The reservation ran out, was released, or does not exist: no copy is held for this order any more.</summary>
    public sealed record Gone : HoldResult;
}

public abstract record ConfirmResult
{
    private ConfirmResult()
    {
    }

    public sealed record Confirmed(int CopyNumber) : ConfirmResult;

    /// <summary>Inventory will not give a copy: the reservation expired, was released, or does not exist.</summary>
    public sealed record Refused : ConfirmResult;
}

/// <summary>
/// Orders' calls to Inventory's reservation endpoints (ADR 0013). Each is safe to repeat. A call that gets no answer throws
/// <see cref="NewStore.Messaging.TransientFailureException"/>.
/// </summary>
public sealed class InventoryClient(HttpClient http)
{
    private const string ExpiredType = "urn:newstore:problem:reservation-expired";
    private const string ReleasedType = "urn:newstore:problem:reservation-released";

    public async Task<ReserveResult> ReserveAsync(Guid editionId, Guid reservationId, Guid buyerId, CancellationToken ct)
    {
        using var response = await ServiceCall.PostAsync(
            http, $"/editions/{editionId}/reservations", new { reservationId, buyerId }, ct);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
                return new ReserveResult.Reserved(await ReadInfoAsync(response, ct));

            case HttpStatusCode.OK:
                // A repeat of this reservation id. Inventory answers 200 with the reservation as it is now (ADR 0013).
                var info = await ReadInfoAsync(response, ct);
                return info.Status switch
                {
                    "Pending" => new ReserveResult.Reserved(info),
                    "Expired" => new ReserveResult.Rejected(409, ExpiredType, "The reservation ran out of time."),
                    "Released" => new ReserveResult.Rejected(409, ReleasedType, "The reservation was released."),
                    // Held and Confirmed need a hold or confirm call, which Orders makes only after it has the 201 or this 200.
                    _ => throw new InvalidOperationException($"Reserve answered 200 {info.Status} for the new reservation {reservationId}."),
                };

            case HttpStatusCode.NotFound or HttpStatusCode.Conflict:
                var (type, title) = await ServiceCall.ReadProblemAsync(response, ct);
                return new ReserveResult.Rejected((int)response.StatusCode, type, title);

            default:
                // A 400 or 403 is a bug in Orders or in the setup, not something to retry and not something to show the buyer.
                throw new HttpRequestException($"Reserve answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    public async Task<HoldResult> HoldAsync(Guid reservationId, CancellationToken ct)
    {
        using var response = await ServiceCall.PostAsync(http, $"/reservations/{reservationId}/hold", null, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => new HoldResult.Held((await ReadInfoAsync(response, ct)).ExpiresAt),
            HttpStatusCode.Conflict or HttpStatusCode.NotFound => new HoldResult.Gone(),
            _ => throw new HttpRequestException($"Hold answered {(int)response.StatusCode}.", null, response.StatusCode),
        };
    }

    public async Task<ConfirmResult> ConfirmAsync(Guid reservationId, CancellationToken ct)
    {
        using var response = await ServiceCall.PostAsync(http, $"/reservations/{reservationId}/confirm", null, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var body = await response.Content.ReadFromJsonAsync<ReservationBody>(ct);
                return new ConfirmResult.Confirmed(
                    body?.CopyNumber ?? throw new InvalidOperationException("Confirm answered 200 without a copy number."));
            case HttpStatusCode.Conflict or HttpStatusCode.NotFound:
                // No reservation, no copy: refuse, and the saga refunds.
                return new ConfirmResult.Refused();
            default:
                throw new HttpRequestException($"Confirm answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct)
    {
        using var response = await ServiceCall.PostAsync(http, $"/reservations/{reservationId}/release", null, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent or HttpStatusCode.NotFound:
                return;
            case HttpStatusCode.Conflict:
                // Inventory confirmed the copy, so this order should never have asked to release it. A bug: do not retry.
                throw new InvalidOperationException($"Release of {reservationId} answered 409: the reservation is confirmed.");
            default:
                throw new HttpRequestException($"Release answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    private static async Task<ReservationInfo> ReadInfoAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<ReservationBody>(ct)
            ?? throw new InvalidOperationException("Inventory answered with an empty body.");
        return new ReservationInfo(body.Id, body.VideoId, body.Format, body.Price, body.Status, body.ExpiresAt);
    }

    /// <summary>The part of Inventory's <c>ReservationResponse</c> Orders reads. JSON names are matched without regard to case.</summary>
    private sealed record ReservationBody(
        Guid Id, Guid VideoId, string Format, decimal Price, string Status, DateTimeOffset ExpiresAt, int? CopyNumber);
}
