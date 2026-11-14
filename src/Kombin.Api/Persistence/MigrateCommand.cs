using Kombin.Api.Common.Time;

namespace Kombin.Api.Persistence;

/// <summary>
/// <c>dotnet Kombin.Api.dll migrate</c> (in compose: <c>docker compose run --rm --no-deps api migrate</c>): applies the pending EF migrations and exits.
/// Outside Development the API never migrates at start-up, and the runtime image has no <c>dotnet ef</c>, so this is how a VPS is updated.
/// It builds only what the database needs (configuration and the DbContext), so it runs before the Production settings check and needs nothing
/// but the connection string.
/// </summary>
internal static class MigrateCommand
{
    public const string Name = "migrate";

    public static bool IsRequested(string[] args) => args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(args[1..]);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.AddKombinPersistence();

        // Only the DbContext is used here; the seeders AddKombinPersistence also registers need services this command does not build, and
        // Development's validation of the whole container would refuse them.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });

        try
        {
            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

            if (pending.Count == 0)
            {
                await output.WriteLineAsync("The database is up to date.");
                return 0;
            }

            await db.Database.MigrateAsync();
            await output.WriteLineAsync($"Applied {pending.Count} migration(s): {string.Join(", ", pending)}");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Migration failed: {exception.Message}");
            return 1;
        }
    }
}
