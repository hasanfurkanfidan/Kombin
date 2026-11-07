using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Social;

internal sealed class PostTagConfiguration : IEntityTypeConfiguration<PostTag>
{
    public void Configure(EntityTypeBuilder<PostTag> builder)
    {
        builder.ToTable("post_tags", "social", table =>
        {
            table.HasCheckConstraint("ck_post_tags_box", "x >= 0 AND y >= 0 AND w > 0 AND h > 0 AND x + w <= 1.0001 AND y + h <= 1.0001");
            table.HasCheckConstraint(
                "ck_post_tags_kind",
                "(kind = 'Product' AND product_id IS NOT NULL) OR (kind = 'Wardrobe' AND wardrobe_item_id IS NOT NULL) OR kind = 'Unknown' OR confirmed_at IS NULL");
        });

        builder.HasKey(tag => tag.Id);
        builder.Property(tag => tag.Id).ValueGeneratedNever();

        builder.Property(tag => tag.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(tag => tag.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(tag => tag.Color).HasConversion<string>().HasMaxLength(20);
        builder.Property(tag => tag.Label).HasMaxLength(PostTag.MaxLabelLength);
        builder.Property(tag => tag.PriceAtTag).HasPrecision(12, 2);

        builder.HasIndex(tag => new { tag.PostId, tag.Position });

        // "Which posts tag this product" (attribution on /go checks post + product).
        builder.HasIndex(tag => tag.ProductId).HasFilter("product_id IS NOT NULL");

        // A wardrobe item that is deleted, or a product that leaves the catalog, leaves the tag behind without its link.
        builder.HasOne<WardrobeItem>()
            .WithMany()
            .HasForeignKey(tag => tag.WardrobeItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(tag => tag.ProductId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Offer>()
            .WithMany()
            .HasForeignKey(tag => tag.OfferId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(tag => tag.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
