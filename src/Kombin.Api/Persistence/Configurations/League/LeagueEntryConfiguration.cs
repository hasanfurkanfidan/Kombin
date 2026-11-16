using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.League;

internal sealed class LeagueEntryConfiguration : IEntityTypeConfiguration<LeagueEntry>
{
    public void Configure(EntityTypeBuilder<LeagueEntry> builder)
    {
        builder.ToTable("entries", "league", table =>
        {
            table.HasCheckConstraint("ck_entries_owner", "(kind = 'Human') = (user_id IS NOT NULL)");
            table.HasCheckConstraint("ck_entries_reason", "(status = 'NotCompeting') = (reason IS NOT NULL)");
            table.HasCheckConstraint("ck_entries_counters", "exposures >= 0 AND comparisons >= 0 AND counting_reports >= 0 AND weighted_wins >= 0 AND weighted_wins <= weighted_games");
            table.HasCheckConstraint("ck_entries_percentile", "percentile IS NULL OR percentile BETWEEN 0 AND 1");
        });

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Occasion).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.Reason).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.PieceSetHash).HasMaxLength(LeagueEntry.PieceSetHashLength);

        // One outfit per player per day.
        builder.HasIndex(entry => new { entry.Day, entry.UserId }).IsUnique().HasFilter("user_id IS NOT NULL");

        // Pairing: the day's competing entries by bucket, least exposed first.
        builder.HasIndex(entry => new { entry.Day, entry.Status, entry.Audience, entry.Occasion, entry.Exposures });

        // The repeat rule: a player's entries of the last 30 days.
        builder.HasIndex(entry => new { entry.UserId, entry.Day });

        builder.HasMany(entry => entry.Pieces).WithOne().HasForeignKey(piece => piece.EntryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(entry => entry.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Outfit>().WithMany().HasForeignKey(entry => entry.OutfitId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class LeagueEntryPieceConfiguration : IEntityTypeConfiguration<LeagueEntryPiece>
{
    public void Configure(EntityTypeBuilder<LeagueEntryPiece> builder)
    {
        builder.ToTable("entry_pieces", "league", table =>
            table.HasCheckConstraint("ck_entry_pieces_image", "image_key IS NULL OR image_url IS NULL"));

        builder.HasKey(piece => new { piece.EntryId, piece.Position });
        builder.Property(piece => piece.Slot).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.ImageKey).HasMaxLength(LeagueEntryPiece.MaxImageKeyLength);
        builder.Property(piece => piece.ImageUrl).HasMaxLength(LeagueEntryPiece.MaxImageUrlLength);

        // Account deletion and the purge read the copies to delete.
        builder.HasIndex(piece => piece.ImageKey).HasFilter("image_key IS NOT NULL");

        builder.HasOne<WardrobeItem>().WithMany().HasForeignKey(piece => piece.WardrobeItemId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Product>().WithMany().HasForeignKey(piece => piece.ProductId).OnDelete(DeleteBehavior.SetNull);
    }
}
