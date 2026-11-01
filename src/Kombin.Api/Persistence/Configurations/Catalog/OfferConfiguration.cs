using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class OfferConfiguration : IEntityTypeConfiguration<Offer>
{
    public void Configure(EntityTypeBuilder<Offer> builder)
    {
        builder.ToTable("offers", "catalog");

        builder.HasKey(offer => offer.Id);
        builder.Property(offer => offer.Id).ValueGeneratedNever();

        builder.Property(offer => offer.ExternalId).HasMaxLength(200).IsRequired();
        builder.Property(offer => offer.Url).HasMaxLength(2048).IsRequired();
        builder.Property(offer => offer.Title).HasMaxLength(300).IsRequired();
        builder.Property(offer => offer.CurrentPrice).HasPrecision(12, 2);
        builder.Property(offer => offer.OriginalPrice).HasPrecision(12, 2);
        builder.Property(offer => offer.Currency).HasMaxLength(3).IsFixedLength().HasDefaultValue(Offer.DefaultCurrency).IsRequired();
        builder.Property(offer => offer.ShippingCost).HasPrecision(12, 2);
        builder.Property(offer => offer.FreeShippingThreshold).HasPrecision(12, 2);
        builder.Property(offer => offer.StockStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(offer => offer.AvailableSizes).HasColumnType("text[]");

        builder.HasIndex(offer => new { offer.SellerId, offer.ExternalId }).IsUnique();
        builder.HasIndex(offer => new { offer.ProductId, offer.IsActive, offer.CurrentPrice });

        builder.HasOne(offer => offer.Seller)
            .WithMany()
            .HasForeignKey(offer => offer.SellerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
