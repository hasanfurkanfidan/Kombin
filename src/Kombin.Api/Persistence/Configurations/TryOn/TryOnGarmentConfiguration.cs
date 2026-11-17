using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.TryOn;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.TryOn;

internal sealed class TryOnGarmentConfiguration : IEntityTypeConfiguration<TryOnGarment>
{
    public void Configure(EntityTypeBuilder<TryOnGarment> builder)
    {
        builder.ToTable("garments", "tryon", table =>
        {
            table.HasCheckConstraint("ck_garments_owner", "(wardrobe_item_id IS NULL) <> (product_id IS NULL)");
            table.HasCheckConstraint("ck_garments_attempts", "attempts >= 0");
        });

        builder.HasKey(garment => garment.Id);
        builder.Property(garment => garment.Id).ValueGeneratedNever();

        builder.Property(garment => garment.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(garment => garment.SourceHash).HasMaxLength(TryOnGarment.HashLength);
        builder.Property(garment => garment.PreparedHash).HasMaxLength(TryOnGarment.HashLength);
        builder.Property(garment => garment.PrepareVersion).HasMaxLength(40);
        builder.Property(garment => garment.Family).HasMaxLength(20);
        builder.Property(garment => garment.Source).HasMaxLength(20);
        builder.Property(garment => garment.SleevesJson).HasColumnName("sleeves").HasColumnType("jsonb");
        builder.Property(garment => garment.LandmarksJson).HasColumnName("landmarks").HasColumnType("jsonb");
        builder.Property(garment => garment.CutoutKey).HasMaxLength(300);
        builder.Property(garment => garment.LastError).HasMaxLength(TryOnGarment.MaxErrorLength);

        // One row per owner: the prepare claims it, requests and the job find it by its owner.
        builder.HasIndex(garment => garment.WardrobeItemId).IsUnique().HasFilter("wardrobe_item_id IS NOT NULL");
        builder.HasIndex(garment => garment.ProductId).IsUnique().HasFilter("product_id IS NOT NULL");

        // "The current version" is the latest answer's.
        builder.HasIndex(garment => garment.PreparedAt);

        // The wardrobe item's deletion (and so the account's) takes its row with it; catalogue rows go only with their product.
        builder.HasOne<WardrobeItem>()
            .WithMany()
            .HasForeignKey(garment => garment.WardrobeItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(garment => garment.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
