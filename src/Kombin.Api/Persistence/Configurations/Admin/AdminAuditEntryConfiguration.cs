using Kombin.Api.Persistence.Entities.Admin;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Admin;

internal sealed class AdminAuditEntryConfiguration : IEntityTypeConfiguration<AdminAuditEntry>
{
    public void Configure(EntityTypeBuilder<AdminAuditEntry> builder)
    {
        builder.ToTable("audit_log", "admin");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Actor).HasMaxLength(AdminAuditEntry.MaxActorLength).IsRequired();
        builder.Property(entry => entry.Action).HasMaxLength(AdminAuditEntry.MaxActionLength).IsRequired();
        builder.Property(entry => entry.TargetType).HasMaxLength(AdminAuditEntry.MaxTargetTypeLength);
        builder.Property(entry => entry.TargetId).HasMaxLength(AdminAuditEntry.MaxTargetIdLength);
        builder.Property(entry => entry.Summary).HasMaxLength(AdminAuditEntry.MaxSummaryLength);

        // The audit page: newest first.
        builder.HasIndex(entry => entry.CreatedAt).IsDescending();
    }
}
