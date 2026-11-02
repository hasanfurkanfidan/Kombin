using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Recommendation;

internal sealed class OutfitNarrationConfiguration : IEntityTypeConfiguration<OutfitNarration>
{
    public void Configure(EntityTypeBuilder<OutfitNarration> builder)
    {
        builder.ToTable("narrations", "recommendation");

        builder.HasKey(narration => narration.Hash);
        builder.Property(narration => narration.Hash).HasMaxLength(64);
        builder.Property(narration => narration.Text).HasMaxLength(Outfit.MaxNarrationLength);
        builder.Property(narration => narration.Model).HasMaxLength(100);
    }
}
