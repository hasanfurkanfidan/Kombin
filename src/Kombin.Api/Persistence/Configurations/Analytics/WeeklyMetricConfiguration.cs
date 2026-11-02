using Kombin.Api.Persistence.Entities.Analytics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Analytics;

internal sealed class WeeklyMetricConfiguration : IEntityTypeConfiguration<WeeklyMetric>
{
    public void Configure(EntityTypeBuilder<WeeklyMetric> builder)
    {
        builder.ToTable("weekly_metrics", "analytics");

        builder.HasKey(metric => new { metric.WeekStart, metric.Metric });

        builder.Property(metric => metric.Metric).HasMaxLength(40);
        builder.Property(metric => metric.Value).HasPrecision(7, 2);
    }
}
