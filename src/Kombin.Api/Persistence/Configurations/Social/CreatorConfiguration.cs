using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Social;

internal sealed class CreatorProfileConfiguration : IEntityTypeConfiguration<CreatorProfile>
{
    public void Configure(EntityTypeBuilder<CreatorProfile> builder)
    {
        builder.ToTable("creator_profiles", "social");

        builder.HasKey(creator => creator.UserId);
        builder.Property(creator => creator.Handle).HasMaxLength(CreatorProfile.MaxHandleLength);
        builder.Property(creator => creator.Bio).HasMaxLength(CreatorProfile.MaxBioLength);
        builder.Property(creator => creator.Link).HasMaxLength(CreatorProfile.MaxLinkLength);

        builder.HasIndex(creator => creator.Handle).IsUnique();

        builder.HasOne<User>().WithOne().HasForeignKey<CreatorProfile>(creator => creator.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReferralEventConfiguration : IEntityTypeConfiguration<ReferralEvent>
{
    public void Configure(EntityTypeBuilder<ReferralEvent> builder)
    {
        builder.ToTable("referral_events", "social", table =>
            table.HasCheckConstraint("ck_referral_events_share", "share_percent BETWEEN 0 AND 100"));

        builder.HasKey(referral => referral.Id);
        builder.Property(referral => referral.Id).UseIdentityAlwaysColumn();
        builder.Property(referral => referral.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(referral => referral.SharePercent).HasPrecision(5, 2);
        builder.Property(referral => referral.OrderAmount).HasPrecision(12, 2);
        builder.Property(referral => referral.CommissionAmount).HasPrecision(12, 2);
        builder.Property(referral => referral.CreatorAmount).HasPrecision(12, 2);
        builder.Property(referral => referral.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(referral => referral.ExternalOrderRef).HasMaxLength(120);

        // The creator's stats; one click row per click; one conversion per order and seller (re-imports are idempotent).
        builder.HasIndex(referral => new { referral.CreatorId, referral.OccurredAt });
        builder.HasIndex(referral => referral.PostId);
        builder.HasIndex(referral => referral.ClickId).IsUnique().HasFilter("kind = 'Click'");
        builder.HasIndex(referral => new { referral.SellerId, referral.ExternalOrderRef }).IsUnique().HasFilter("external_order_ref IS NOT NULL");

        // Earnings outlive accounts and catalog cleanups, like clicks: both the creator and the viewer are anonymized (set null) when their
        // account is deleted, and the amounts stay as the financial record.
        builder.HasOne<User>().WithMany().HasForeignKey(referral => referral.CreatorId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(referral => referral.ViewerUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Click>().WithMany().HasForeignKey(referral => referral.ClickId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(referral => referral.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Offer>().WithMany().HasForeignKey(referral => referral.OfferId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Seller>().WithMany().HasForeignKey(referral => referral.SellerId).OnDelete(DeleteBehavior.Restrict);
    }
}
