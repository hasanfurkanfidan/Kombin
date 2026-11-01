using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Notification;

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences", "notification");

        builder.HasKey(preference => preference.UserId);
        builder.Property(preference => preference.UserId).ValueGeneratedNever();

        builder.Property(preference => preference.QuietHoursStart).HasColumnType("time without time zone");
        builder.Property(preference => preference.QuietHoursEnd).HasColumnType("time without time zone");

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<NotificationPreference>(preference => preference.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
