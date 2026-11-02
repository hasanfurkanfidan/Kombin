using System.Text.Json;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence.Entities.Notification;

namespace Kombin.Api.Tests;

public class NotificationDataTests
{
    [Fact]
    public void ThePushPayloadIsTheStoredObjectPlusTheNotificationIdAndKind()
    {
        var productId = Guid.NewGuid();
        var offerId = Guid.NewGuid();

        var payload = NotificationData.ForPush(NotificationData.ForAlert(productId, offerId, alertId: null), 42, NotificationKind.PriceDrop);

        Assert.Equal(42, payload.GetProperty("notificationId").GetInt64());
        Assert.Equal("PriceDrop", payload.GetProperty("kind").GetString());
        Assert.Equal(productId, payload.GetProperty("productId").GetGuid());
        Assert.Equal(offerId, payload.GetProperty("offerId").GetGuid());
        Assert.Equal($"kombin://product/{productId}", payload.GetProperty("deepLink").GetString());
    }

    [Fact]
    public void StoredKeysOfTheSameNameAreOverwrittenAndANonObjectIsReplaced()
    {
        var overwritten = NotificationData.ForPush("""{"notificationId":1,"kind":"Old","deepLink":"kombin://x"}""", 7, NotificationKind.System);
        Assert.Equal(7, overwritten.GetProperty("notificationId").GetInt64());
        Assert.Equal("System", overwritten.GetProperty("kind").GetString());
        Assert.Equal("kombin://x", overwritten.GetProperty("deepLink").GetString());

        var replaced = NotificationData.ForPush("[1,2]", 8, NotificationKind.NewMatches);
        Assert.Equal(JsonValueKind.Object, replaced.ValueKind);
        Assert.Equal(8, replaced.GetProperty("notificationId").GetInt64());
        Assert.Equal("NewMatches", replaced.GetProperty("kind").GetString());
    }
}
