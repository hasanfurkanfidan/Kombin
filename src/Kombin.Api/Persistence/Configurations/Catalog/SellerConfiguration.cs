using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class SellerConfiguration : IEntityTypeConfiguration<Seller>
{
    public void Configure(EntityTypeBuilder<Seller> builder)
    {
        builder.ToTable("sellers", "catalog");

        builder.HasKey(seller => seller.Id);
        builder.Property(seller => seller.Id).ValueGeneratedNever();

        builder.Property(seller => seller.Name).HasMaxLength(100).IsRequired();
        builder.Property(seller => seller.Slug).HasMaxLength(120).IsRequired();
        builder.Property(seller => seller.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(seller => seller.WebsiteUrl).HasMaxLength(2048).IsRequired();
        builder.Property(seller => seller.AffiliateNetwork).HasMaxLength(50);
        builder.Property(seller => seller.IsActive).HasDefaultValue(true);

        builder.HasIndex(seller => seller.Name).IsUnique();
        builder.HasIndex(seller => seller.Slug).IsUnique();

        builder.HasData(
            new Seller
            {
                Id = CatalogSeed.SellerId(1),
                Name = "Trendyol",
                Slug = "trendyol",
                Kind = SellerKind.Marketplace,
                WebsiteUrl = "https://www.trendyol.com",
                AffiliateNetwork = "trendyol-partner",
                IsActive = true,
                CreatedAt = CatalogSeed.SeededAt,
                UpdatedAt = CatalogSeed.SeededAt,
            },
            new Seller
            {
                Id = CatalogSeed.SellerId(2),
                Name = "Hepsiburada",
                Slug = "hepsiburada",
                Kind = SellerKind.Marketplace,
                WebsiteUrl = "https://www.hepsiburada.com",
                IsActive = true,
                CreatedAt = CatalogSeed.SeededAt,
                UpdatedAt = CatalogSeed.SeededAt,
            });
    }
}
