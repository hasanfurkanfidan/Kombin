using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.TryOn;

internal sealed class TryOnRenderConfiguration : IEntityTypeConfiguration<TryOnRender>
{
    public void Configure(EntityTypeBuilder<TryOnRender> builder)
    {
        builder.ToTable("renders", "tryon", table =>
            table.HasCheckConstraint("ck_renders_image_or_refusal", "(image_key IS NULL) <> (refusal IS NULL)"));

        builder.HasKey(render => render.Id);
        builder.Property(render => render.Id).ValueGeneratedNever();

        builder.Property(render => render.CacheKey).HasMaxLength(TryOnRender.HashLength);
        builder.Property(render => render.BodyHash).HasMaxLength(TryOnRender.HashLength);
        builder.Property(render => render.Renderer).HasMaxLength(TryOnRender.MaxRendererLength);
        builder.Property(render => render.ImageKey).HasMaxLength(300);
        builder.Property(render => render.Refusal).HasMaxLength(40);
        builder.Property(render => render.GarmentsJson).HasColumnName("garments").HasColumnType("jsonb");

        // The cache: one render per user and key.
        builder.HasIndex(render => new { render.UserId, render.CacheKey }).IsUnique();

        // The retention sweep.
        builder.HasIndex(render => render.LastUsedAt);

        // Renders reveal body proportions: they go with the account.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(render => render.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
