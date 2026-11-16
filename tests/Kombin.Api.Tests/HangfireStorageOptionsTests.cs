using Kombin.Api.Features.Notification;

namespace Kombin.Api.Tests;

public sealed class HangfireStorageOptionsTests
{
    /// <summary>
    /// Task B7: without the sliding timeout Hangfire.PostgreSql hands a job that is still running (the nightly embedding run lasts up to three
    /// hours, the fixed invisibility timeout is 30 minutes) to a second worker.
    /// </summary>
    [Fact]
    public void ARunningJobStaysInvisibleToOtherWorkers()
    {
        var options = NotificationModule.StorageOptions();

        Assert.True(options.UseSlidingInvisibilityTimeout);
        Assert.True(options.EnableLongPolling);
        Assert.Equal(NotificationModule.HangfireSchema, options.SchemaName);
    }
}
