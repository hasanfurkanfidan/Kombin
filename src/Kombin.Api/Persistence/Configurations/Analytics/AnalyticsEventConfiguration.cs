using Kombin.Api.Persistence.Entities.Analytics;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Analytics;

internal sealed class AnalyticsEventConfiguration : IEntityTypeConfiguration<AnalyticsEvent>
{
    public void Configure(EntityTypeBuilder<AnalyticsEvent> builder)
    {
        builder.ToTable("events", "analytics");

        builder.HasKey(analyticsEvent => analyticsEvent.Id);
        builder.Property(analyticsEvent => analyticsEvent.Id).UseIdentityAlwaysColumn();

        builder.Property(analyticsEvent => analyticsEvent.AnonymousId).HasMaxLength(64);
        builder.Property(analyticsEvent => analyticsEvent.Name).HasMaxLength(60).IsRequired();
        builder.Property(analyticsEvent => analyticsEvent.Properties).HasColumnType("jsonb").IsRequired();
        builder.Property(analyticsEvent => analyticsEvent.SessionId).HasMaxLength(64).IsRequired();
        builder.Property(analyticsEvent => analyticsEvent.Platform).HasConversion<string>().HasMaxLength(10);
        builder.Property(analyticsEvent => analyticsEvent.AppVersion).HasMaxLength(40).IsRequired();

        builder.HasIndex(analyticsEvent => new { analyticsEvent.Name, analyticsEvent.OccurredAt });
        builder.HasIndex(analyticsEvent => new { analyticsEvent.UserId, analyticsEvent.OccurredAt });

        // What the identity linker looks up when a device signs in: only the rows still waiting for their user.
        builder.HasIndex(analyticsEvent => analyticsEvent.AnonymousId)
            .HasFilter("user_id is null and anonymous_id is not null");

        // Events outlive the account (anonymized), like clicks.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(analyticsEvent => analyticsEvent.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
