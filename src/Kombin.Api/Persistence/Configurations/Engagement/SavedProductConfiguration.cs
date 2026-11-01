using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Engagement;

internal sealed class SavedProductConfiguration : IEntityTypeConfiguration<SavedProduct>
{
    public void Configure(EntityTypeBuilder<SavedProduct> builder)
    {
        builder.ToTable("saved_products", "engagement");

        builder.HasKey(saved => new { saved.UserId, saved.ProductId });

        builder.HasIndex(saved => new { saved.UserId, saved.CreatedAt }).IsDescending(false, true);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(saved => saved.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(saved => saved.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
