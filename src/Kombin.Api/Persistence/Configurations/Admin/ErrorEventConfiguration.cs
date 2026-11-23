using Kombin.Api.Persistence.Entities.Admin;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Admin;

internal sealed class ErrorEventConfiguration : IEntityTypeConfiguration<ErrorEvent>
{
    public void Configure(EntityTypeBuilder<ErrorEvent> builder)
    {
        builder.ToTable("error_events", "admin");

        builder.HasKey(error => error.Id);
        builder.Property(error => error.Id).ValueGeneratedNever();

        builder.Property(error => error.Kind).HasMaxLength(ErrorEvent.MaxKindLength).IsRequired();
        builder.Property(error => error.Source).HasMaxLength(ErrorEvent.MaxSourceLength).IsRequired();
        builder.Property(error => error.ErrorCode).HasMaxLength(ErrorEvent.MaxErrorCodeLength).IsRequired();

        // The system page reads the last 24 hours and the last failure per job; the purge deletes by age.
        builder.HasIndex(error => error.CreatedAt);
    }
}
