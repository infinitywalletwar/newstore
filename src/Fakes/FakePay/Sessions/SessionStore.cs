namespace NewStore.FakePay.Sessions;

public enum PayOutcome { NotFound, Paid, NotOpen }

public enum RefundOutcome { NotFound, NotCompleted, Refunded }

/// <summary>All of FakePay's state, in memory. One lock guards it: this is a test double, and the lock keeps each rule easy to read.</summary>
public sealed class SessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CheckoutSession> _sessions = [];
    private readonly Dictionary<string, string> _sessionByKey = [];
    private readonly Dictionary<string, Refund> _refundByKey = [];

    /// <summary>
    /// The session for <paramref name="idempotencyKey"/>. The first call creates it with <paramref name="create"/>; a later call
    /// with the same key returns that session and ignores the new arguments, as Stripe does for a repeated request.
    /// </summary>
    public CheckoutSession GetOrCreate(string idempotencyKey, Func<string, CheckoutSession> create)
    {
        lock (_gate)
        {
            if (_sessionByKey.TryGetValue(idempotencyKey, out var existingId))
            {
                return _sessions[existingId];
            }

            var session = create($"cs_{Guid.NewGuid():N}");
            _sessions[session.Id] = session;
            _sessionByKey[idempotencyKey] = session.Id;
            return session;
        }
    }

    public CheckoutSession? Find(string id)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Completes an open session. A session whose time ran out is not payable even when the sweeper has not marked it yet, so the
    /// answer does not depend on the sweeper's timing.
    /// </summary>
    public PayOutcome Pay(string id, DateTimeOffset now, out CheckoutSession? session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out session))
            {
                return PayOutcome.NotFound;
            }

            if (session.Status != SessionStatus.Open || session.ExpiresAt <= now)
            {
                return PayOutcome.NotOpen;
            }

            session = session with { Status = SessionStatus.Complete };
            _sessions[id] = session;
            return PayOutcome.Paid;
        }
    }

    /// <summary>Marks every open session whose time ran out as expired and returns them.</summary>
    public IReadOnlyList<CheckoutSession> ExpireDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            var expired = new List<CheckoutSession>();
            foreach (var session in _sessions.Values.Where(s => s.Status == SessionStatus.Open && s.ExpiresAt <= now).ToList())
            {
                var changed = session with { Status = SessionStatus.Expired };
                _sessions[session.Id] = changed;
                expired.Add(changed);
            }

            return expired;
        }
    }

    /// <summary>Refunds a completed session. The same <paramref name="idempotencyKey"/> returns the same refund.</summary>
    public RefundOutcome Refund(string idempotencyKey, string sessionId, out Refund? refund)
    {
        lock (_gate)
        {
            if (_refundByKey.TryGetValue(idempotencyKey, out refund))
            {
                return RefundOutcome.Refunded;
            }

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return RefundOutcome.NotFound;
            }

            if (session.Status != SessionStatus.Complete)
            {
                return RefundOutcome.NotCompleted;
            }

            refund = new Refund($"re_{Guid.NewGuid():N}", session.Id, session.Amount);
            _refundByKey[idempotencyKey] = refund;
            return RefundOutcome.Refunded;
        }
    }
}
