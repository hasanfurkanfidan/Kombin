using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class SizeEntryConfiguration : IEntityTypeConfiguration<SizeEntry>
{
    public void Configure(EntityTypeBuilder<SizeEntry> builder)
    {
        builder.ToTable("size_entries", "profile");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Group).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Size).HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Confidence).HasPrecision(3, 2);

        builder.HasIndex(entry => new { entry.UserId, entry.BrandId, entry.Group }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Brand>()
            .WithMany()
            .HasForeignKey(entry => entry.BrandId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
