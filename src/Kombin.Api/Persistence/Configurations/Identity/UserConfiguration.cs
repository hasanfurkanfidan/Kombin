using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", "identity");

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        builder.Property(user => user.Email).HasMaxLength(320).IsRequired();
        builder.Property(user => user.PasswordHash).HasMaxLength(512);
        builder.Property(user => user.DisplayName).HasMaxLength(50).IsRequired();

        builder.HasIndex(user => user.Email).IsUnique();

        // What the account-deletion job scans: only the few accounts waiting to be removed.
        builder.HasIndex(user => user.DeletionRequestedAt).HasFilter("deletion_requested_at IS NOT NULL");
    }
}
