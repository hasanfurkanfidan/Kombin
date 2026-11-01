using Kombin.Api.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kombin.Api.Common.Health;

internal sealed class PostgresHealthCheck(KombinDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
    }
}
