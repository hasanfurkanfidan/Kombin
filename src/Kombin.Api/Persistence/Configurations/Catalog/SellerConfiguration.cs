using System.Text.Json;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
        builder.Property(seller => seller.AffiliateUrlTemplate).HasMaxLength(1024);

        // Stored as a jsonb object. A converter (rather than Npgsql's dynamic JSON) keeps the data source free of extra switches.
        builder.Property(seller => seller.AffiliateParams)
            .HasColumnType("jsonb")
            .HasConversion(
                new ValueConverter<Dictionary<string, string>?, string?>(
                    parameters => parameters == null ? null : JsonSerializer.Serialize(parameters, (JsonSerializerOptions?)null),
                    json => json == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null)),
                new ValueComparer<Dictionary<string, string>?>(
                    (left, right) => left == null ? right == null : right != null && left.Count == right.Count && !left.Except(right).Any(),
                    parameters => parameters == null ? 0 : parameters.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    parameters => parameters == null ? null : new Dictionary<string, string>(parameters)));

        builder.Property(seller => seller.IsActive).HasDefaultValue(true);
        builder.Property(seller => seller.Country).HasColumnType("char(2)").HasDefaultValue("TR").IsRequired();

        builder.HasIndex(seller => seller.Name).IsUnique();
        builder.HasIndex(seller => seller.Slug).IsUnique();

        // The seeded sellers have no AffiliateUrlTemplate, so /go/{offerId} uses the default utm_* wrapping for them.
        // Real affiliate templates and params are configured per seller later, once the programmes are signed.
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
