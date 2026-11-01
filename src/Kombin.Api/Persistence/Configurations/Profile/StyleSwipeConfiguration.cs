using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class StyleSwipeConfiguration : IEntityTypeConfiguration<StyleSwipe>
{
    public void Configure(EntityTypeBuilder<StyleSwipe> builder)
    {
        builder.ToTable("style_swipes", "profile");

        builder.HasKey(swipe => swipe.Id);
        builder.Property(swipe => swipe.Id).UseIdentityAlwaysColumn();

        builder.Property(swipe => swipe.Decision).HasConversion<string>().HasMaxLength(10);

        // Doubles as the lookup index for all swipes of a persona.
        builder.HasIndex(swipe => new { swipe.PersonaId, swipe.StyleCardId }).IsUnique();

        builder.HasOne<Persona>()
            .WithMany()
            .HasForeignKey(swipe => swipe.PersonaId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<StyleCard>()
            .WithMany()
            .HasForeignKey(swipe => swipe.StyleCardId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
