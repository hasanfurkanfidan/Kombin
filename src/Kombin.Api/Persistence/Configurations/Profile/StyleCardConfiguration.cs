using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class StyleCardConfiguration : IEntityTypeConfiguration<StyleCard>
{
    public void Configure(EntityTypeBuilder<StyleCard> builder)
    {
        builder.ToTable("style_cards", "profile", table =>
            table.HasCheckConstraint("ck_style_cards_formality", "formality BETWEEN 1 AND 5"));

        // The ID comes from style-cards.json, never from the database.
        builder.HasKey(card => card.Id);
        builder.Property(card => card.Id).ValueGeneratedNever();

        builder.Property(card => card.Title).HasMaxLength(80).IsRequired();
        builder.Property(card => card.ImageUrl).HasMaxLength(2048).IsRequired();
        builder.Property(card => card.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(card => card.Family).HasConversion<string>().HasMaxLength(20);
        builder.Property(card => card.Colors).HasColumnType("text[]");
        builder.Property(card => card.Fits).HasColumnType("text[]");
        builder.Property(card => card.Seasons).HasConversion<short>();

        builder.HasIndex(card => new { card.IsActive, card.SortOrder });
    }
}
