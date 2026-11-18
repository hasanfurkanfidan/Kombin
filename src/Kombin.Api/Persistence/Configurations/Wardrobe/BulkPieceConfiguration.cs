using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Wardrobe;

internal sealed class BulkPieceConfiguration : IEntityTypeConfiguration<BulkPiece>
{
    public void Configure(EntityTypeBuilder<BulkPiece> builder)
    {
        builder.ToTable("bulk_pieces", "wardrobe", table =>
            table.HasCheckConstraint("ck_bulk_pieces_formality", "formality BETWEEN 1 AND 5"));

        builder.HasKey(piece => piece.Id);

        // The ID is the wardrobe item's ID at confirm (a retry can never make two items), so it is set by the job, never by the database.
        builder.Property(piece => piece.Id).ValueGeneratedNever();

        builder.Property(piece => piece.PrimaryColor).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.SecondaryColor).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.Pattern).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.CutoutKey).HasMaxLength(BulkBatch.MaxKeyLength);
        builder.Property(piece => piece.ThumbKey).HasMaxLength(BulkBatch.MaxKeyLength);
        builder.Property(piece => piece.EmbeddingModel).HasMaxLength(100);
        builder.Property(piece => piece.AnalysisJson).HasColumnType("jsonb");
        builder.Property(piece => piece.Embedding).HasColumnType($"vector({WardrobeItem.EmbeddingDimensions})");

        // Seasons is a flags enum stored as its integer value (smallint), as on items.
        builder.Property(piece => piece.Seasons).HasConversion<short>();

        // A batch's pieces in reading order.
        builder.HasIndex(piece => new { piece.BatchId, piece.SortOrder }).IsUnique();

        builder.HasOne<BulkBatch>()
            .WithMany()
            .HasForeignKey(piece => piece.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(piece => piece.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A piece is deleted at confirm and cancel: a category cleanup must not be blocked by one, the piece just loses the guess.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(piece => piece.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
