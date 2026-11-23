using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Admin;

namespace Kombin.Api.Common.Diagnostics;

/// <summary>
/// Where an unhandled error goes: Sentry (when a DSN is set, through <see cref="ErrorReporting"/>) and a row in <c>admin.error_events</c> that
/// <c>/admin/system</c> counts. The row is in the database rather than in memory so the counts survive a restart and a deploy; it holds the kind, the
/// route template or job name and the exception's type, nothing else. Never throws: reporting an error must not cause a second one.
/// </summary>
internal sealed partial class ErrorLog(IServiceScopeFactory scopes, IClock clock, ILogger<ErrorLog> logger)
{
    public const string RequestKind = "request";

    public const string JobKind = "job";

    /// <summary>How long a row is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private long purgedAtTicks;

    public async Task ReportAsync(Exception exception, string kind, string source, string? correlationId, CancellationToken cancellationToken = default)
    {
        var errorCode = ErrorReporting.ErrorCode(exception);
        source = Truncate(source, ErrorEvent.MaxSourceLength);

        ErrorReporting.Capture(exception, kind, source, errorCode, correlationId);
        await RecordAsync(kind, source, Truncate(errorCode, ErrorEvent.MaxErrorCodeLength), cancellationToken);
    }

    internal async Task RecordAsync(string kind, string source, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            // A fresh scope: the request's own DbContext may be the thing that failed.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

            db.ErrorEvents.Add(new ErrorEvent { Kind = kind, Source = source, ErrorCode = errorCode });
            await db.SaveChangesAsync(cancellationToken);

            await PurgeIfDueAsync(db, cancellationToken);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogRecordFailed(failure.GetType().Name);
        }
    }

    /// <summary>At most once an hour per instance, after a write: errors are rare, so a job of its own would cost more than it saves.</summary>
    private async Task PurgeIfDueAsync(KombinDbContext db, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var last = Interlocked.Read(ref purgedAtTicks);
        if (now.UtcTicks - last < PurgeInterval.Ticks || Interlocked.CompareExchange(ref purgedAtTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        var cutoff = now - Retention;
        await db.ErrorEvents.Where(error => error.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record an error for the system page ({FailureType}).")]
    private partial void LogRecordFailed(string failureType);
}
