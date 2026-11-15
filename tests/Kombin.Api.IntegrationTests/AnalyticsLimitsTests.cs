using System.Diagnostics.Metrics;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Analytics.Jobs;
using Kombin.Api.Features.Notification;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>Security review M6: the public event intake has a daily cap per actor, and raw events are deleted after the retention period.</summary>
public sealed class AnalyticsLimitsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EmptyJson = "{}";

    private static readonly int Cap = new AnalyticsOptions().DailyEventCapPerActor;

    [Fact]
    public async Task AnActorOverItsDailyCapIsDroppedWith202AndOthersInTheBatchAreNot()
    {
        using var client = factory.CreateAnonymousClient();
        var capped = AnalyticsClientExtensions.NewAnonymousId();
        var other = AnalyticsClientExtensions.NewAnonymousId();
        await InsertEventsAsync(capped, Cap - 2, DateTimeOffset.UtcNow);
        long counted = 0;
        using var listener = Listen(value => Interlocked.Add(ref counted, value));

        var body = await client.PostEventsOkAsync(
            [
                AnalyticsClientExtensions.Event(anonymousId: capped),
                AnalyticsClientExtensions.Event(anonymousId: other),
                AnalyticsClientExtensions.Event(anonymousId: capped),
                AnalyticsClientExtensions.Event(anonymousId: capped),
                AnalyticsClientExtensions.Event(anonymousId: other),
            ],
            Ct);

        Assert.Equal(4, body.GetProperty("accepted").GetInt32());
        var rejected = Assert.Single(body.GetProperty("rejected").EnumerateArray());
        Assert.Equal((3, RejectionReasons.DailyCap), (rejected.GetProperty("index").GetInt32(), rejected.GetProperty("reason").GetString()));
        Assert.Equal(Cap, await CountAsync(capped));
        Assert.Equal(2, await CountAsync(other));
        Assert.True(Interlocked.Read(ref counted) >= 1);
    }

    [Fact]
    public async Task ASignedInUserIsCappedByAccountAndYesterdayDoesNotCount()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO analytics.events (user_id, name, properties, session_id, platform, app_version, occurred_at, received_at)
            SELECT {user.Id}, 'app_open', CAST({EmptyJson} AS jsonb), 'session-cap', 'Ios', '1.0.0', {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}
            FROM generate_series(1, {Cap})
            """,
            Ct));

        var full = await user.Client.PostEventsOkAsync([AnalyticsClientExtensions.Event()], Ct);
        Assert.Equal(0, full.GetProperty("accepted").GetInt32());
        Assert.Equal(RejectionReasons.DailyCap, full.GetProperty("rejected")[0].GetProperty("reason").GetString());

        // A device whose whole cap was spent yesterday (Istanbul time) starts again today.
        using var anonymous = factory.CreateAnonymousClient();
        var device = AnalyticsClientExtensions.NewAnonymousId();
        await InsertEventsAsync(device, Cap, EventCap.DayStart(DateTimeOffset.UtcNow, QuietHours.Zone).AddMinutes(-1));
        var fresh = await anonymous.PostEventsOkAsync([AnalyticsClientExtensions.Event(anonymousId: device)], Ct);
        Assert.Equal(1, fresh.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task TheRetentionJobDeletesEventsPastTheRetentionPeriodOnly()
    {
        var device = AnalyticsClientExtensions.NewAnonymousId();
        var days = new AnalyticsOptions().RetentionDays;
        await InsertEventsAsync(device, 3, DateTimeOffset.UtcNow.AddDays(-days - 1));
        await InsertEventsAsync(device, 2, DateTimeOffset.UtcNow.AddDays(-days + 1));

        var deleted = await factory.RunJobAsync<AnalyticsRetentionJob, int>(job => job.PurgeAsync([device], Ct));

        Assert.Equal(3, deleted);
        Assert.Equal(2, await CountAsync(device));
        Assert.Equal(0, await factory.RunJobAsync<AnalyticsRetentionJob, int>(job => job.PurgeAsync([device], Ct)));
    }

    private Task<int> InsertEventsAsync(string anonymousId, int count, DateTimeOffset receivedAt) =>
        factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO analytics.events (anonymous_id, name, properties, session_id, platform, app_version, occurred_at, received_at)
            SELECT {anonymousId}, 'app_open', CAST({EmptyJson} AS jsonb), 'session-' || n, 'Ios', '1.0.0', {receivedAt}, {receivedAt}
            FROM generate_series(1, {count}) AS n
            """,
            Ct));

    private Task<int> CountAsync(string anonymousId) =>
        factory.WithDbAsync(db => db.AnalyticsEvents.CountAsync(row => row.AnonymousId == anonymousId, Ct));

    private static MeterListener Listen(Action<long> onCapped)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AnalyticsMetrics.MeterName && instrument.Name == "kombin.analytics.events_capped")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => onCapped(value));
        listener.Start();

        return listener;
    }
}
