using Kombin.Api.Features.Catalog.Embeddings;

namespace Kombin.Api.Tests;

public class PoliteScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OneHostGetsOneRequestPerSecond()
    {
        var schedule = new PoliteSchedule(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, schedule.Reserve("img.test", Now));
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Reserve("img.test", Now));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.Reserve("img.test", Now));

        // Time that passed counts: half a second later the next slot (at +3 s) is 2.5 s away.
        Assert.Equal(TimeSpan.FromSeconds(2.5), schedule.Reserve("IMG.TEST", Now.AddSeconds(0.5)));
    }

    [Fact]
    public void HostsDoNotWaitForEachOther()
    {
        var schedule = new PoliteSchedule(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, schedule.Reserve("a.test", Now));
        Assert.Equal(TimeSpan.Zero, schedule.Reserve("b.test", Now));
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Reserve("a.test", Now));
    }

    [Fact]
    public void AnIdleHostIsFreeAgain()
    {
        var schedule = new PoliteSchedule(TimeSpan.FromSeconds(1));
        schedule.Reserve("img.test", Now);

        Assert.Equal(TimeSpan.Zero, schedule.Reserve("img.test", Now.AddSeconds(5)));
    }

    [Fact]
    public void RetryAfterPushesTheHostBackButNeverForward()
    {
        var schedule = new PoliteSchedule(TimeSpan.FromSeconds(1));
        schedule.Reserve("img.test", Now);
        schedule.PushBack("img.test", Now, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), schedule.Reserve("img.test", Now));

        // A shorter Retry-After does not pull the already later slot (now +31 s) forward.
        schedule.PushBack("img.test", Now, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(31), schedule.Reserve("img.test", Now));
    }

    [Fact]
    public void WorkIsInterleavedAcrossHostsKeepingEachHostsOrder()
    {
        string[] urls = ["a1", "a2", "a3", "b1", "c1", "c2"];

        var ordered = PoliteSchedule.Interleave(urls, url => url[..1]);

        Assert.Equal(["a1", "b1", "c1", "a2", "c2", "a3"], ordered);
    }
}
