using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Notification.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.IntegrationTests;

internal static class NotificationClientExtensions
{
    /// <summary>A valid Expo token whose <paramref name="flavor"/> tells <see cref="FakeExpoPushClient"/> how to answer for it.</summary>
    public static string NewToken(string flavor = "ok") => $"ExponentPushToken[{flavor}-{Guid.NewGuid():N}]";

    public static Task<HttpResponseMessage> PutDeviceAsync(
        this HttpClient client, string? token, string platform = "Android", string? appVersion = "1.0.0", string? deviceName = "Pixel", CancellationToken cancellationToken = default) =>
        client.PutAsJsonAsync("/api/v1/me/devices", new { expoPushToken = token, platform, appVersion, deviceName }, cancellationToken);

    /// <summary>Registers a device and asserts the 204.</summary>
    public static async Task<string> RegisterDeviceAsync(this HttpClient client, string? token = null, CancellationToken cancellationToken = default)
    {
        token ??= NewToken();
        using var response = await client.PutDeviceAsync(token, cancellationToken: cancellationToken);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(cancellationToken));

        return token;
    }

    public static Task<HttpResponseMessage> DeleteDeviceAsync(this HttpClient client, string token, CancellationToken cancellationToken = default) =>
        client.DeleteAsync($"/api/v1/me/devices/{Uri.EscapeDataString(token)}", cancellationToken);

    public static Task<HttpResponseMessage> PutPreferencesAsync(this HttpClient client, object body, CancellationToken cancellationToken = default) =>
        client.PutAsJsonAsync("/api/v1/me/notification-preferences", body, cancellationToken);

    public static async Task<JsonElement> FeedAsync(this HttpClient client, CancellationToken cancellationToken = default) =>
        (await client.GetJsonAsync("/api/v1/me/notifications?pageSize=100", cancellationToken)).GetProperty("items");

    public static async Task<int> UnreadCountAsync(this HttpClient client, CancellationToken cancellationToken = default) =>
        (await client.GetJsonAsync("/api/v1/me/notifications/unread-count", cancellationToken)).GetProperty("count").GetInt32();

    public static async Task<HttpResponseMessage> PostInternalAsync(this HttpClient client, string url, object body, string? internalKey = ApiFactory.InternalApiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };

        if (internalKey is not null)
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Runs <paramref name="action"/> against a job resolved from a fresh scope, the way Hangfire would activate it.</summary>
    public static async Task<TResult> RunJobAsync<TJob, TResult>(this ApiFactory factory, Func<TJob, Task<TResult>> action)
        where TJob : notnull
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    public static Task<DispatchSummary> DispatchAlertsAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<AlertDispatcherJob, DispatchSummary>(job => job.DispatchAsync(userId, cancellationToken));

    public static Task<PushRunResult> SendPushesAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<PushSenderJob, PushRunResult>(job => job.SendDueAsync(userId, null, cancellationToken));

    public static Task<int> CheckReceiptsAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<PushReceiptsJob, int>(job => job.CheckAsync(userId, cancellationToken));

    /// <summary>Runs the job for one user over <paramref name="products"/>; null means every new product of the shared database.</summary>
    public static Task<int> SendWeeklyMatchesAsync(this ApiFactory factory, Guid userId, IReadOnlyCollection<Guid>? products, CancellationToken cancellationToken) =>
        factory.RunJobAsync<WeeklyMatchesJob, int>(job => job.SendAsync(userId, products, cancellationToken));

    /// <summary>Runs <paramref name="action"/> with the database, for arranging and checking state that has no endpoint.</summary>
    public static async Task<TResult> WithDbAsync<TResult>(this ApiFactory factory, Func<KombinDbContext, Task<TResult>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<KombinDbContext>());
    }

    public static Task<List<NotificationEntity>> NotificationsOfAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(notification => notification.UserId == userId).OrderBy(notification => notification.Id).ToListAsync(cancellationToken));

    public static Task<List<Device>> DevicesWithTokenAsync(this ApiFactory factory, string token, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.Devices.AsNoTracking().Where(device => device.ExpoPushToken == token).OrderBy(device => device.CreatedAt).ToListAsync(cancellationToken));

    /// <summary>Inserts a notification directly, for tests of the push sender and the receipts job.</summary>
    public static Task<long> InsertNotificationAsync(
        this ApiFactory factory,
        Guid userId,
        NotificationStatus status = NotificationStatus.Pending,
        DateTimeOffset? scheduledFor = null,
        DateTimeOffset? sentAt = null,
        string? ticketIds = null,
        NotificationKind kind = NotificationKind.System,
        CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var notification = new NotificationEntity
            {
                UserId = userId,
                Kind = kind,
                Title = "Test",
                Body = "Test body",
                Data = """{"deepLink":"kombin://notifications"}""",
                DedupeKey = $"test:{Guid.NewGuid():N}",
                CreatedAt = now,
                ScheduledFor = scheduledFor ?? now.AddSeconds(-5),
                SentAt = sentAt,
                Status = status,
                ExpoTicketId = ticketIds,
            };

            db.Notifications.Add(notification);
            await db.SaveChangesAsync(cancellationToken);

            return notification.Id;
        });
}
