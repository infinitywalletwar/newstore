using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NewStore.Inventory;

/// <summary>
/// The logger factory EF Core writes to. It hides the two Error records that EF logs for the unique violation
/// the reserve strategies handle (<see cref="ReserveSupport.IsReservationConflict(DbUpdateException)"/>: answered as a 409, or as the 200 of
/// a parallel request with the same id) and
/// passes every other record on unchanged. Register it as a singleton: EF Core asks for one factory used by every context.
/// </summary>
/// <remarks>
/// The two records come from different categories, and only one of them carries the exception:
/// <list type="bullet">
/// <item><c>SaveChangesFailed</c> (Microsoft.EntityFrameworkCore.Update) carries the <see cref="DbUpdateException"/>,
/// so the exact violation is checked.</item>
/// <item><c>CommandError</c> (Microsoft.EntityFrameworkCore.Database.Command) carries no exception, only the SQL. It is
/// hidden for every command that inserts into <c>reservations</c>. ConcurrencyToken inserts with SaveChanges, so its other
/// failures are still logged by <c>SaveChangesFailed</c>. ConditionalUpdate inserts in its one reserve statement, which is
/// not a save: any failure other than the handled violation leaves the strategy as an exception, and the exception handler
/// middleware (<c>UseExceptionHandler</c> in ServiceDefaults) logs it.</item>
/// </list>
/// </remarks>
internal sealed class HandledConflictLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private const string ReservationInsert = "INSERT INTO reservations ";

    public ILogger CreateLogger(string categoryName) => categoryName switch
    {
        "Microsoft.EntityFrameworkCore.Update" or "Microsoft.EntityFrameworkCore.Database.Command"
            => new HandledConflictLogger(inner.CreateLogger(categoryName)),
        _ => inner.CreateLogger(categoryName),
    };

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    // The host owns the inner factory.
    public void Dispose()
    {
    }

    private sealed class HandledConflictLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsHandled(eventId, state, exception, formatter))
            {
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);
        }

        private static bool IsHandled<TState>(
            EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            eventId.Id == CoreEventId.SaveChangesFailed.Id
                ? exception is DbUpdateException ex && ReserveSupport.IsReservationConflict(ex)
                : eventId.Id == RelationalEventId.CommandError.Id
                    && formatter(state, exception).Contains(ReservationInsert, StringComparison.Ordinal);
    }
}
