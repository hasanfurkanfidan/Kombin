using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Recommendation;

internal sealed class OutfitFeedbackConfiguration : IEntityTypeConfiguration<OutfitFeedback>
{
    public void Configure(EntityTypeBuilder<OutfitFeedback> builder)
    {
        builder.ToTable("outfit_feedback", "recommendation");

        builder.HasKey(feedback => feedback.Id);
        builder.Property(feedback => feedback.Id).UseIdentityAlwaysColumn();

        builder.Property(feedback => feedback.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(feedback => feedback.Reason).HasConversion<string>().HasMaxLength(30);

        builder.HasIndex(feedback => feedback.OutfitId);
        builder.HasIndex(feedback => new { feedback.UserId, feedback.CreatedAt });

        builder.HasOne<Outfit>()
            .WithMany()
            .HasForeignKey(feedback => feedback.OutfitId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(feedback => feedback.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
