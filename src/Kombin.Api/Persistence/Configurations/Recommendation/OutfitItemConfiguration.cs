using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Recommendation;

internal sealed class OutfitItemConfiguration : IEntityTypeConfiguration<OutfitItem>
{
    public void Configure(EntityTypeBuilder<OutfitItem> builder)
    {
        builder.ToTable("outfit_items", "recommendation", table =>
            table.HasCheckConstraint("ck_outfit_items_source", "(wardrobe_item_id IS NULL) <> (product_id IS NULL)"));

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).UseIdentityAlwaysColumn();

        builder.Property(item => item.Slot).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Price).HasPrecision(12, 2);

        builder.HasIndex(item => item.OutfitId);

        // Wardrobe items are archived and products deactivated, never deleted in normal operation; a hard delete takes the piece row with it.
        builder.HasOne<WardrobeItem>()
            .WithMany()
            .HasForeignKey(item => item.WardrobeItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Offer>()
            .WithMany()
            .HasForeignKey(item => item.OfferId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
