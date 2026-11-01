using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Engagement;

internal sealed class AlertTriggerConfiguration : IEntityTypeConfiguration<AlertTrigger>
{
    public void Configure(EntityTypeBuilder<AlertTrigger> builder)
    {
        builder.ToTable("alert_triggers", "engagement");

        builder.HasKey(trigger => trigger.Id);
        builder.Property(trigger => trigger.Id).UseIdentityByDefaultColumn();

        builder.Property(trigger => trigger.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(trigger => trigger.PriceBefore).HasPrecision(12, 2);
        builder.Property(trigger => trigger.PriceAfter).HasPrecision(12, 2);
        builder.Property(trigger => trigger.Payload).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(trigger => new { trigger.UserId, trigger.TriggeredAt }).IsDescending(false, true);
        builder.HasIndex(trigger => trigger.PriceAlertId);

        // Deleting an alert keeps its history in the feed.
        builder.HasOne<PriceAlert>()
            .WithMany()
            .HasForeignKey(trigger => trigger.PriceAlertId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(trigger => trigger.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(trigger => trigger.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Offer>()
            .WithMany()
            .HasForeignKey(trigger => trigger.OfferId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
