using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.TryOn;

internal sealed class TryOnReportConfiguration : IEntityTypeConfiguration<TryOnReport>
{
    public void Configure(EntityTypeBuilder<TryOnReport> builder)
    {
        builder.ToTable("reports", "tryon", table =>
            table.HasCheckConstraint("ck_reports_subject", "(outfit_id IS NULL) <> (product_id IS NULL)"));

        builder.HasKey(report => report.Id);
        builder.Property(report => report.Id).UseIdentityAlwaysColumn();
        builder.Property(report => report.Renderer).HasMaxLength(TryOnRender.MaxRendererLength);

        // One report per user and drawing; the outfit has no foreign key (like a wear's outfit). A deleted drawing leaves its reports (render_id null).
        builder.HasIndex(report => new { report.UserId, report.RenderId }).IsUnique();

        builder.HasOne<TryOnRender>()
            .WithMany()
            .HasForeignKey(report => report.RenderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(report => report.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
