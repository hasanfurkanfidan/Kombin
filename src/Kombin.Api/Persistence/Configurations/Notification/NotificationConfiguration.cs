using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Notification;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Entities.Notification.Notification>
{
    public void Configure(EntityTypeBuilder<Entities.Notification.Notification> builder)
    {
        builder.ToTable("notifications", "notification");

        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).UseIdentityByDefaultColumn();

        builder.Property(notification => notification.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(notification => notification.Title).HasMaxLength(120).IsRequired();
        builder.Property(notification => notification.Body).HasMaxLength(300).IsRequired();
        builder.Property(notification => notification.Data).HasColumnType("jsonb").IsRequired();
        builder.Property(notification => notification.DedupeKey).HasMaxLength(120).IsRequired();
        builder.Property(notification => notification.Status).HasConversion<string>().HasMaxLength(12);
        builder.Property(notification => notification.FailureReason).HasMaxLength(300);
        builder.Property(notification => notification.ExpoTicketId).HasMaxLength(100);

        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(notification => new { notification.Status, notification.ScheduledFor });

        // The 24-hour dedupe window cannot be a partial unique index (now() is not immutable), so the dispatcher checks it with this index.
        builder.HasIndex(notification => new { notification.UserId, notification.DedupeKey, notification.CreatedAt }).IsDescending(false, false, true);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The trigger is only the source; deleting it must not take the user's notification away.
        builder.HasOne(notification => notification.AlertTrigger)
            .WithMany()
            .HasForeignKey(notification => notification.AlertTriggerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
