using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace NewStore.Orders.Tests;

internal static class StubReplies
{
    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string slug, string title = "A problem.")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = $"urn:newstore:problem:{slug}", title, status = (int)status }),
                System.Text.Encoding.UTF8,
                "application/problem+json"),
        };
        return response;
    }

    public static HttpResponseMessage Unavailable() => new(HttpStatusCode.ServiceUnavailable);

    /// <summary>The body of a request, as the stub saw it.</summary>
    public static JsonElement Body(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()).RootElement;

    /// <summary>The id between two path segments, for example the reservation id in <c>/reservations/{id}/hold</c>.</summary>
    public static Guid IdAfter(HttpRequestMessage request, string segment)
    {
        var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        return Guid.Parse(parts[Array.IndexOf(parts, segment) + 1]);
    }
}

/// <summary>The calls both stubs received, in the order they arrived, so a test can assert which call came first.</summary>
internal sealed class CallLog
{
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public List<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }
}

/// <summary>
/// Stands in for Inventory's reservation endpoints. The defaults behave like Inventory (ADR 0013): a first reserve is 201 and a repeat
/// of the same id is 200 <c>Pending</c>; hold and confirm answer 200; confirm repeats give the same copy number; release is 204. A
/// test replaces one delegate to change the answer. The stub sits below the resilience handler, so it sees every retry.
/// </summary>
internal sealed class InventoryStub
{
    private readonly ConcurrentDictionary<Guid, int> _copies = new();
    private readonly ConcurrentDictionary<Guid, (Guid EditionId, Guid BuyerId)> _reserved = new();
    private int _nextCopy;

    public InventoryStub(CallLog log)
    {
        Handler = new StubHttpHandler()
            .On(HttpMethod.Post, "/editions/{id}/reservations", r => Reserve(r))
            .On(HttpMethod.Post, "/reservations/{id}/hold", r =>
            {
                log.Add($"hold {StubReplies.IdAfter(r, "reservations")}");
                return Hold(r);
            })
            .On(HttpMethod.Post, "/reservations/{id}/confirm", r => Confirm(r))
            .On(HttpMethod.Post, "/reservations/{id}/release", r => Release(r));
        Reset();
    }

    public StubHttpHandler Handler { get; }

    public Func<HttpRequestMessage, HttpResponseMessage> Reserve { get; set; } = null!;

    public Func<HttpRequestMessage, HttpResponseMessage> Hold { get; set; } = null!;

    public Func<HttpRequestMessage, HttpResponseMessage> Confirm { get; set; } = null!;

    public Func<HttpRequestMessage, HttpResponseMessage> Release { get; set; } = null!;

    public void Reset()
    {
        Reserve = r =>
        {
            var body = StubReplies.Body(r);
            var id = body.GetProperty("reservationId").GetGuid();
            var buyer = body.GetProperty("buyerId").GetGuid();
            var edition = StubReplies.IdAfter(r, "editions");
            var first = _reserved.TryAdd(id, (edition, buyer));
            return StubReplies.Json(first ? HttpStatusCode.Created : HttpStatusCode.OK, Reservation(id, edition, buyer, "Pending", null));
        };
        Hold = r => StubReplies.Json(HttpStatusCode.OK, Reservation(StubReplies.IdAfter(r, "reservations"), Guid.Empty, Guid.Empty, "Held", null, held: true));
        Confirm = r =>
        {
            var id = StubReplies.IdAfter(r, "reservations");
            var copy = _copies.GetOrAdd(id, _ => Interlocked.Increment(ref _nextCopy));
            return StubReplies.Json(HttpStatusCode.OK, Reservation(id, Guid.Empty, Guid.Empty, "Confirmed", copy));
        };
        Release = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static object Reservation(Guid id, Guid editionId, Guid buyerId, string status, int? copyNumber, bool held = false) => new
    {
        id,
        editionId,
        buyerId,
        videoId = VideoId,
        format = "Hd",
        price = 9.99m,
        status,
        reservedAt = WholeMicrosecond(DateTimeOffset.UtcNow),
        expiresAt = WholeMicrosecond(DateTimeOffset.UtcNow.AddMinutes(held ? 45 : 10)),
        copyNumber,
    };

    public static readonly Guid VideoId = Guid.NewGuid();

    private static DateTimeOffset WholeMicrosecond(DateTimeOffset v) => new(v.UtcTicks - (v.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>The requests Orders sent for one reservation, as <c>"POST /reservations/{id}/hold"</c>.</summary>
    public List<HttpRequestMessage> RequestsFor(Guid reservationId) =>
        [.. Handler.Requests.Where(r => r.RequestUri!.AbsolutePath.Contains(reservationId.ToString()))];

    /// <summary>The reserve requests for an edition, which a test gave an id of its own.</summary>
    public List<HttpRequestMessage> ReservesFor(Guid editionId) =>
        [.. Handler.Requests.Where(r => r.RequestUri!.AbsolutePath == $"/editions/{editionId}/reservations")];
}

/// <summary>Stands in for Payments: 201 for the first create of an order, 200 for every repeat, with the same payment.</summary>
internal sealed class PaymentsStub
{
    private readonly ConcurrentDictionary<Guid, Guid> _payments = new();

    public PaymentsStub(CallLog log)
    {
        Handler = new StubHttpHandler().On(HttpMethod.Post, "/payments", r =>
        {
            log.Add($"create {StubReplies.Body(r).GetProperty("orderId").GetGuid()}");
            return Create(r);
        });
        Reset();
    }

    public StubHttpHandler Handler { get; }

    public Func<HttpRequestMessage, HttpResponseMessage> Create { get; set; } = null!;

    public void Reset() =>
        Create = r =>
        {
            var body = StubReplies.Body(r);
            var orderId = body.GetProperty("orderId").GetGuid();
            var first = _payments.TryAdd(orderId, Guid.CreateVersion7());
            var id = _payments[orderId];
            return StubReplies.Json(first ? HttpStatusCode.Created : HttpStatusCode.OK, new
            {
                id,
                orderId,
                amount = body.GetProperty("amount").GetDecimal(),
                status = "Created",
                checkoutUrl = $"http://fakepay.test/checkout/{id}",
                sessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            });
        };

    /// <summary>The id of the one payment this stub made for an order, or null when it made none.</summary>
    public Guid? PaymentIdFor(Guid orderId) => _payments.TryGetValue(orderId, out var id) ? id : null;

    /// <summary>The create requests for an order, with their bodies already read.</summary>
    public List<HttpRequestMessage> CreatesFor(Guid orderId) =>
        [.. Handler.Requests.Where(r => StubReplies.Body(r).GetProperty("orderId").GetGuid() == orderId)];
}
