using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Notification;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Notification;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices", "notification");

        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).ValueGeneratedNever();

        builder.Property(device => device.ExpoPushToken).HasMaxLength(200).IsRequired();
        builder.Property(device => device.Platform).HasConversion<string>().HasMaxLength(10);
        builder.Property(device => device.AppVersion).HasMaxLength(40);
        builder.Property(device => device.DeviceName).HasMaxLength(100);
        builder.Property(device => device.DeactivationReason).HasMaxLength(100);

        // Unique among active rows only: a moved or removed token keeps its history row next to the live one.
        builder.HasIndex(device => device.ExpoPushToken)
            .IsUnique()
            .HasFilter("is_active");
        builder.HasIndex(device => new { device.UserId, device.IsActive });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(device => device.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
