using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Recommendation;

internal sealed class OutfitConfiguration : IEntityTypeConfiguration<Outfit>
{
    public void Configure(EntityTypeBuilder<Outfit> builder)
    {
        builder.ToTable("outfits", "recommendation", table =>
            table.HasCheckConstraint("ck_outfits_score", "score BETWEEN 0 AND 100"));

        builder.HasKey(outfit => outfit.Id);
        builder.Property(outfit => outfit.Id).ValueGeneratedNever();

        builder.Property(outfit => outfit.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(outfit => outfit.Completeness).HasConversion<string>().HasMaxLength(20);
        builder.Property(outfit => outfit.Occasion).HasConversion<string>().HasMaxLength(20);
        builder.Property(outfit => outfit.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(outfit => outfit.NarrationProvider).HasConversion<string>().HasMaxLength(20);
        builder.Property(outfit => outfit.DismissReason).HasConversion<string>().HasMaxLength(30);
        builder.Property(outfit => outfit.WeatherJson).HasColumnType("jsonb");
        builder.Property(outfit => outfit.Rule).HasMaxLength(40);
        builder.Property(outfit => outfit.Hash).HasMaxLength(64);
        builder.Property(outfit => outfit.NarrationHash).HasMaxLength(64);
        builder.Property(outfit => outfit.Narration).HasMaxLength(Outfit.MaxNarrationLength);
        builder.Property(outfit => outfit.NarrationInput).HasColumnType("jsonb");

        // Reusing a repeated suggestion, and the dismissed-hash filter.
        builder.HasIndex(outfit => new { outfit.UserId, outfit.Hash });

        // GET /saved, newest first.
        builder.HasIndex(outfit => new { outfit.UserId, outfit.SavedAt }).HasFilter("saved_at IS NOT NULL");

        builder.HasMany(outfit => outfit.Items)
            .WithOne()
            .HasForeignKey(item => item.OutfitId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(outfit => outfit.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Persona>()
            .WithMany()
            .HasForeignKey(outfit => outfit.PersonaId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
