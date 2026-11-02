using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Wardrobe;

internal sealed class WardrobeWearConfiguration : IEntityTypeConfiguration<WardrobeWear>
{
    public void Configure(EntityTypeBuilder<WardrobeWear> builder)
    {
        builder.ToTable("wears", "wardrobe");

        builder.HasKey(wear => wear.Id);
        builder.Property(wear => wear.Id).UseIdentityAlwaysColumn();

        builder.HasIndex(wear => new { wear.ItemId, wear.WornOn }).IsUnique();
        builder.HasIndex(wear => new { wear.UserId, wear.WornOn });

        builder.HasOne<WardrobeItem>()
            .WithMany()
            .HasForeignKey(wear => wear.ItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(wear => wear.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
