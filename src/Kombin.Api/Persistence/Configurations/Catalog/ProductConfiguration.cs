using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", "catalog", table =>
            table.HasCheckConstraint("ck_products_formality", "formality BETWEEN 1 AND 5"));

        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id).ValueGeneratedNever();

        builder.Property(product => product.Name).HasMaxLength(200).IsRequired();
        builder.Property(product => product.Slug).HasMaxLength(250).IsRequired();
        builder.Property(product => product.ModelCode).HasMaxLength(100);
        builder.Property(product => product.Description).HasMaxLength(4000);
        builder.Property(product => product.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(product => product.PrimaryColor).HasConversion<string>().HasMaxLength(20);
        builder.Property(product => product.Pattern).HasConversion<string>().HasMaxLength(20);
        builder.Property(product => product.Fit).HasConversion<string>().HasMaxLength(20);
        builder.Property(product => product.Material).HasMaxLength(200);
        builder.Property(product => product.PrimaryImageUrl).HasMaxLength(2048);

        // Seasons is a flags enum stored as its integer value (smallint).
        builder.Property(product => product.Seasons).HasConversion<short>();

        builder.HasIndex(product => new { product.BrandId, product.Slug }).IsUnique();

        // NULLs are distinct in PostgreSQL unique indexes, so this is "unique within the brand when present".
        builder.HasIndex(product => new { product.BrandId, product.ModelCode }).IsUnique();
        builder.HasIndex(product => product.CategoryId);

        // The outfit engine's gap prefetch: category + colour + formality band of active products, a few dozen rows out of 50k.
        builder.HasIndex(product => new { product.CategoryId, product.PrimaryColor, product.Formality }).HasFilter("is_active");

        builder.HasOne(product => product.Brand)
            .WithMany()
            .HasForeignKey(product => product.BrandId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(product => product.Category)
            .WithMany()
            .HasForeignKey(product => product.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(product => product.Images)
            .WithOne()
            .HasForeignKey(image => image.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(product => product.Offers)
            .WithOne(offer => offer.Product)
            .HasForeignKey(offer => offer.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
