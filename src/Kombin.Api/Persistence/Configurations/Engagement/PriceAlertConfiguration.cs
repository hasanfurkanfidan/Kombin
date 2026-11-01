using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Engagement;

internal sealed class PriceAlertConfiguration : IEntityTypeConfiguration<PriceAlert>
{
    public void Configure(EntityTypeBuilder<PriceAlert> builder)
    {
        builder.ToTable("price_alerts", "engagement");

        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Id).ValueGeneratedNever();

        builder.Property(alert => alert.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(alert => alert.TargetPrice).HasPrecision(12, 2);
        builder.Property(alert => alert.Sizes).HasColumnType("text[]");

        // The evaluator looks alerts up by product every time an offer of it changes.
        builder.HasIndex(alert => new { alert.ProductId, alert.IsActive });
        builder.HasIndex(alert => new { alert.UserId, alert.CreatedAt }).IsDescending(false, true);

        // One live alert per user, product and kind; switched-off alerts may pile up.
        builder.HasIndex(alert => new { alert.UserId, alert.ProductId, alert.Kind })
            .IsUnique()
            .HasFilter("is_active");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(alert => alert.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(alert => alert.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
