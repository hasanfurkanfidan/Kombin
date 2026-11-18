using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Wardrobe;

internal sealed class BulkBatchConfiguration : IEntityTypeConfiguration<BulkBatch>
{
    public void Configure(EntityTypeBuilder<BulkBatch> builder)
    {
        builder.ToTable("bulk_batches", "wardrobe");

        builder.HasKey(batch => batch.Id);
        builder.Property(batch => batch.Id).ValueGeneratedNever();

        builder.Property(batch => batch.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(batch => batch.FailureCode).HasConversion<string>().HasMaxLength(30);
        builder.Property(batch => batch.OriginalKey).HasMaxLength(BulkBatch.MaxKeyLength);
        builder.Property(batch => batch.Refusal).HasMaxLength(40);
        builder.Property(batch => batch.FailureDetail).HasMaxLength(300);
        builder.Property(batch => batch.UnseparatedRegions).HasColumnType("jsonb");
        builder.Property(batch => batch.EmbeddingModel).HasMaxLength(100);

        // The user's batches by age: the open-batch check and the daily allowance.
        builder.HasIndex(batch => new { batch.UserId, batch.CreatedAt });

        // The cleanup looks for batches whose staged objects may still exist.
        builder.HasIndex(batch => batch.ExpiresAt).HasFilter("objects_deleted_at IS NULL");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(batch => batch.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
