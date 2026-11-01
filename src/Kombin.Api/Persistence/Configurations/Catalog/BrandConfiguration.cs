using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("brands", "catalog");

        builder.HasKey(brand => brand.Id);
        builder.Property(brand => brand.Id).ValueGeneratedNever();

        builder.Property(brand => brand.Name).HasMaxLength(100).IsRequired();
        builder.Property(brand => brand.Slug).HasMaxLength(100).IsRequired();
        builder.Property(brand => brand.LogoUrl).HasMaxLength(2048);
        builder.Property(brand => brand.WebsiteUrl).HasMaxLength(2048);

        builder.HasIndex(brand => brand.Name).IsUnique();
        builder.HasIndex(brand => brand.Slug).IsUnique();
    }
}
