using System.Globalization;
using Kombin.Api.Common.Time;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The shifted test clock (<see cref="TestClock"/>): unset it is the system clock; set, the run starts at the requested Istanbul time and the
/// API's clock reads the same time as the tests'. Part of every shifted run, so a run proves its own shift.
/// </summary>
public sealed class TestClockTests(ApiFactory factory)
{
    [Fact]
    public void TheTestsAndTheApiReadTheSameClockAndAShiftStartsAtTheRequestedIstanbulTime()
    {
        var requested = Environment.GetEnvironmentVariable(TestClock.Variable);

        if (string.IsNullOrWhiteSpace(requested))
        {
            Assert.Equal(TimeSpan.Zero, TestClock.Shift);
        }
        else
        {
            var started = TimeZoneInfo.ConvertTime(TestClock.StartedAt, Istanbul.Zone);
            Assert.Equal(requested.Trim(), started.ToString("HH:mm", CultureInfo.InvariantCulture));
            Assert.InRange(TestClock.Shift.Duration(), TimeSpan.Zero, TimeSpan.FromHours(12.5));
        }

        var api = factory.Services.GetRequiredService<IClock>().UtcNow;
        Assert.InRange(api - TestClock.UtcNow, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(5));
    }
}
