using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kombin.Api.Tests;

public class SeedOnStartupTests
{
    private static async Task<List<(LogLevel Level, string Message)>> RunAsync(string environment, string? flag)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        var provider = new CollectingLoggerProvider();

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(provider);

        if (flag is not null)
        {
            builder.Configuration[PersistenceExtensions.SeedOnStartupKey] = flag;
        }

        // No DbContext or DevSeeder is registered: if the flag were honoured the call would throw instead of returning.
        await using var app = builder.Build();
        await app.SeedDatabaseOnStartupAsync();

        return provider.Entries;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task TheFlagIsIgnoredWithOneWarningOutsideDevelopment(string environment)
    {
        var entries = await RunAsync(environment, "true");

        var warning = Assert.Single(entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(PersistenceExtensions.SeedOnStartupKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(environment, warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", "false")]
    [InlineData("Production", null)]
    [InlineData("Development", "false")]
    public async Task NothingHappensAndNothingIsLoggedWhenTheFlagIsOff(string environment, string? flag)
    {
        Assert.Empty(await RunAsync(environment, flag));
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
