using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles", "profile");

        builder.HasKey(profile => profile.UserId);
        builder.Property(profile => profile.UserId).ValueGeneratedNever();

        builder.Property(profile => profile.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(profile => profile.WeightKg).HasPrecision(4, 1);
        builder.Property(profile => profile.ShoeSizeEu).HasPrecision(4, 1);

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserProfile>(profile => profile.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Archiving never deletes a persona, but a hard delete must not leave a dangling active pointer.
        builder.HasOne<Persona>()
            .WithMany()
            .HasForeignKey(profile => profile.ActivePersonaId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
