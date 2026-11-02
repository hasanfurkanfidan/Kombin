using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Analytics;

/// <summary>What the Identity endpoints know about analytics: after a login or registration, hand over the device's anonymous ID.</summary>
internal interface IAnalyticsIdentityLinker
{
    /// <summary>
    /// Attaches the events the device sent while anonymous to the user, so pre-login behavior joins the user. Does nothing without an
    /// anonymous ID. Never throws (cancellation aside): analytics must not break a sign-in.
    /// </summary>
    Task LinkAsync(Guid userId, string? anonymousId, CancellationToken cancellationToken);
}

internal sealed partial class AnalyticsIdentityLinker(KombinDbContext db, ILogger<AnalyticsIdentityLinker> logger) : IAnalyticsIdentityLinker
{
    public async Task LinkAsync(Guid userId, string? anonymousId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(anonymousId))
        {
            return;
        }

        try
        {
            // UPDATE analytics.events SET user_id = @userId WHERE anonymous_id = @anon AND user_id IS NULL. Events that already belong to
            // a user are never taken over, so signing in on a shared device cannot claim someone else's history.
            await db.AnalyticsEvents
                .Where(analyticsEvent => analyticsEvent.AnonymousId == anonymousId && analyticsEvent.UserId == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(analyticsEvent => analyticsEvent.UserId, userId), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLinkFailed(logger, exception, userId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Linking the anonymous analytics events of user {UserId} failed; the sign-in is not affected.")]
    private static partial void LogLinkFailed(ILogger logger, Exception exception, Guid userId);
}
