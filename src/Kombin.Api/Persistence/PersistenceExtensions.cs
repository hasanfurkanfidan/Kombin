using Kombin.Api.Persistence.Interceptors;

namespace Kombin.Api.Persistence;

internal static class PersistenceExtensions
{
    public const string ConnectionStringName = "Postgres";

    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    public static IHostApplicationBuilder AddKombinPersistence(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AuditInterceptor>();

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
}
