using Kombin.Api.Persistence.Interceptors;
using Kombin.Api.Persistence.Seed;

namespace Kombin.Api.Persistence;

internal static partial class PersistenceExtensions
{
    public const string ConnectionStringName = "Postgres";

    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    public const string SeedOnStartupKey = "Database:SeedOnStartup";

    public static IHostApplicationBuilder AddKombinPersistence(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AuditInterceptor>();
        builder.Services.AddScoped<DevSeeder>();
        builder.Services.AddScoped<StyleCardSeeder>();

        // Configuration is read lazily so values supplied after Program.cs ran (tests, hosting overrides) are honoured.
        builder.Services.AddDbContext<KombinDbContext>((services, options) =>
        {
            var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");

            options
                .UseNpgsql(connectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(services.GetRequiredService<AuditInterceptor>());
        });

        return builder;
    }

    /// <summary>
    /// Applies pending migrations at startup, but only in Development and only when explicitly enabled.
    /// Every other environment migrates with <c>dotnet ef database update</c>.
    /// </summary>
    public static async Task MigrateDatabaseOnStartupAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>(MigrateOnStartupKey))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Seeds the style cards (editorial content) in every environment. Run it after migrations; when migrations are still pending
    /// (outside Development they are applied by hand) it skips with a warning instead of failing the start.
    /// </summary>
    public static async Task SeedStyleCardsOnStartupAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        if ((await db.Database.GetPendingMigrationsAsync()).Any())
        {
            LogStyleCardsSkipped(app.Logger);
            return;
        }

        await scope.ServiceProvider.GetRequiredService<StyleCardSeeder>().SeedAsync();
    }

    /// <summary>
    /// Seeds the development catalog and demo user when <c>Database:SeedOnStartup</c> is set. Run it after migrations.
    /// Outside Development the flag is ignored (with a warning): sample data never reaches a real database.
    /// </summary>
    public static async Task SeedDatabaseOnStartupAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(SeedOnStartupKey))
        {
            return;
        }

        if (!app.Environment.IsDevelopment())
        {
            LogSeedIgnored(app.Logger, app.Environment.EnvironmentName, SeedOnStartupKey);
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Style cards are not seeded: database migrations are pending. Apply them and restart.")]
    private static partial void LogStyleCardsSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Key} is set but the environment is {Environment}, not Development; seeding is skipped.")]
    private static partial void LogSeedIgnored(ILogger logger, string environment, string key);
}
