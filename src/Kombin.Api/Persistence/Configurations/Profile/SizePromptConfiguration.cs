using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class SizePromptConfiguration : IEntityTypeConfiguration<SizePrompt>
{
    public void Configure(EntityTypeBuilder<SizePrompt> builder)
    {
        builder.ToTable("size_prompts", "profile");

        builder.HasKey(prompt => prompt.Id);
        builder.Property(prompt => prompt.Id).ValueGeneratedNever();

        builder.Property(prompt => prompt.Answer).HasConversion<string>().HasMaxLength(20);
        builder.Property(prompt => prompt.AnsweredSize).HasMaxLength(WardrobeItem.MaxSizeLength);

        // One question per product, ever; it is also the lookup of every request (a user's prompts).
        builder.HasIndex(prompt => new { prompt.UserId, prompt.ProductId }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(prompt => prompt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(prompt => prompt.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
