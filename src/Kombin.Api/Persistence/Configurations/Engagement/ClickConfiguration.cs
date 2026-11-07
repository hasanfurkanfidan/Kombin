using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Engagement;

internal sealed class ClickConfiguration : IEntityTypeConfiguration<Click>
{
    public void Configure(EntityTypeBuilder<Click> builder)
    {
        builder.ToTable("clicks", "engagement");

        builder.HasKey(click => click.Id);

        // BY DEFAULT, not ALWAYS: the redirect reserves the id first (it goes into the affiliate URL) and inserts it explicitly.
        builder.Property(click => click.Id).UseIdentityByDefaultColumn();

        builder.Property(click => click.Source).HasMaxLength(40).IsRequired();
        builder.Property(click => click.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(click => click.UserAgent).HasMaxLength(512);
        builder.Property(click => click.IpHash).HasMaxLength(64);
        builder.Property(click => click.RedirectUrl).HasMaxLength(2048).IsRequired();

        builder.HasIndex(click => new { click.OfferId, click.ClickedAt });
        builder.HasIndex(click => new { click.UserId, click.ClickedAt });

        // GET /me/creator/stats.
        builder.HasIndex(click => new { click.CreatorId, click.ClickedAt }).HasFilter("creator_id IS NOT NULL");

        // Clicks are the revenue record: they outlive the user (anonymized) and refuse to disappear with an offer, product or seller.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(click => click.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(click => click.CreatorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Offer>()
            .WithMany()
            .HasForeignKey(click => click.OfferId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(click => click.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Seller>()
            .WithMany()
            .HasForeignKey(click => click.SellerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
