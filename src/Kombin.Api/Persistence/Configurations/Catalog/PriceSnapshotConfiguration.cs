using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class PriceSnapshotConfiguration : IEntityTypeConfiguration<PriceSnapshot>
{
    public void Configure(EntityTypeBuilder<PriceSnapshot> builder)
    {
        builder.ToTable("price_snapshots", "catalog");

        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.Id).UseIdentityByDefaultColumn();

        builder.Property(snapshot => snapshot.Price).HasPrecision(12, 2);
        builder.Property(snapshot => snapshot.OriginalPrice).HasPrecision(12, 2);
        builder.Property(snapshot => snapshot.StockStatus).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(snapshot => new { snapshot.OfferId, snapshot.ObservedAt }).IsDescending(false, true);

        builder.HasOne(snapshot => snapshot.Offer)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.OfferId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
