using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Wardrobe;

internal sealed class WardrobeItemConfiguration : IEntityTypeConfiguration<WardrobeItem>
{
    public void Configure(EntityTypeBuilder<WardrobeItem> builder)
    {
        builder.ToTable("items", "wardrobe", table =>
            table.HasCheckConstraint("ck_items_formality", "formality BETWEEN 1 AND 5"));

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.Name).HasMaxLength(WardrobeItem.MaxNameLength);
        builder.Property(item => item.PrimaryColor).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.SecondaryColor).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Pattern).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Fit).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Size).HasMaxLength(WardrobeItem.MaxSizeLength);
        builder.Property(item => item.PurchasePrice).HasPrecision(12, 2);
        builder.Property(item => item.FailureReason).HasMaxLength(300);
        builder.Property(item => item.ImageOriginalKey).HasMaxLength(300);
        builder.Property(item => item.ImageCutoutKey).HasMaxLength(300);
        builder.Property(item => item.ImageThumbKey).HasMaxLength(300);
        builder.Property(item => item.EmbeddingModel).HasMaxLength(100);
        builder.Property(item => item.AnalysisJson).HasColumnType("jsonb");
        builder.Property(item => item.Embedding).HasColumnType($"vector({WardrobeItem.EmbeddingDimensions})");

        // Seasons is a flags enum stored as its integer value (smallint), as on products.
        builder.Property(item => item.Seasons).HasConversion<short>();

        builder.HasIndex(item => new { item.UserId, item.IsArchived, item.CategoryId });

        // The cleanup job looks for archived items whose images are still stored.
        builder.HasIndex(item => item.ArchivedAt).HasFilter("is_archived");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(item => item.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // A user's wardrobe outlives catalog cleanups: the item just loses the link.
        builder.HasOne<Brand>()
            .WithMany()
            .HasForeignKey(item => item.BrandId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
