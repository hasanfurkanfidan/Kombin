using Kombin.Api.Persistence.Entities.AskFriend;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.AskFriend;

internal sealed class AskConfiguration : IEntityTypeConfiguration<Ask>
{
    public void Configure(EntityTypeBuilder<Ask> builder)
    {
        builder.ToTable("asks", "askfriend", table =>
        {
            table.HasCheckConstraint("ck_asks_counters", "page_views >= 0 AND store_clicks >= 0 AND push_count >= 0");
            table.HasCheckConstraint("ck_asks_closed", "(status = 'Closed') = (closed_at IS NOT NULL)");
        });

        builder.HasKey(ask => ask.Id);
        builder.Property(ask => ask.Id).ValueGeneratedNever();
        builder.Property(ask => ask.Token).HasMaxLength(Ask.TokenLength);
        builder.Property(ask => ask.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(ask => ask.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(ask => ask.ConsentVersion).HasMaxLength(Ask.MaxConsentVersionLength);

        builder.HasIndex(ask => ask.Token).IsUnique();

        // The asker's list, newest first, and the daily limit.
        builder.HasIndex(ask => new { ask.UserId, ask.CreatedAt }).IsDescending(false, true);

        // The purge: asks whose copies may still exist, by expiry.
        builder.HasIndex(ask => ask.ExpiresAt).HasFilter("copies_deleted_at IS NULL");

        builder.HasMany(ask => ask.Pieces).WithOne().HasForeignKey(piece => piece.AskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(ask => ask.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AskPieceConfiguration : IEntityTypeConfiguration<AskPiece>
{
    public void Configure(EntityTypeBuilder<AskPiece> builder)
    {
        builder.ToTable("pieces", "askfriend", table =>
            table.HasCheckConstraint("ck_pieces_image", "copy_key IS NULL OR image_url IS NULL"));

        builder.HasKey(piece => piece.Id);
        builder.Property(piece => piece.Id).ValueGeneratedNever();
        builder.Property(piece => piece.Option).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.Slot).HasConversion<string>().HasMaxLength(20);
        builder.Property(piece => piece.CopyKey).HasMaxLength(AskPiece.MaxCopyKeyLength);
        builder.Property(piece => piece.ImageUrl).HasMaxLength(AskPiece.MaxImageUrlLength);

        builder.HasIndex(piece => new { piece.AskId, piece.Option, piece.Position }).IsUnique();

        builder.HasOne<Outfit>().WithMany().HasForeignKey(piece => piece.OutfitId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<WardrobeItem>().WithMany().HasForeignKey(piece => piece.WardrobeItemId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Product>().WithMany().HasForeignKey(piece => piece.ProductId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AskAnswerConfiguration : IEntityTypeConfiguration<AskAnswer>
{
    public void Configure(EntityTypeBuilder<AskAnswer> builder)
    {
        builder.ToTable("answers", "askfriend");

        builder.HasKey(answer => answer.Id);
        builder.Property(answer => answer.Id).ValueGeneratedNever();
        builder.Property(answer => answer.VoterHash).HasMaxLength(AskAnswer.VoterHashLength);
        builder.Property(answer => answer.Choice).HasConversion<string>().HasMaxLength(20);
        builder.Property(answer => answer.Name).HasMaxLength(AskAnswer.MaxNameLength);
        builder.Property(answer => answer.Note).HasMaxLength(AskAnswer.MaxNoteLength);

        // One answer per browser and ask (an upsert replaces it).
        builder.HasIndex(answer => new { answer.AskId, answer.VoterHash }).IsUnique();

        // The push job: answers the asker has not been told about.
        builder.HasIndex(answer => answer.NotifiedAt).HasFilter("notified_at IS NULL");

        builder.HasOne<Ask>().WithMany().HasForeignKey(answer => answer.AskId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AskReportConfiguration : IEntityTypeConfiguration<AskReport>
{
    public void Configure(EntityTypeBuilder<AskReport> builder)
    {
        builder.ToTable("reports", "askfriend");

        builder.HasKey(report => report.Id).HasName("pk_askfriend_reports");
        builder.Property(report => report.Id).ValueGeneratedNever();
        builder.Property(report => report.VoterHash).HasMaxLength(AskAnswer.VoterHashLength);
        builder.Property(report => report.Reason).HasConversion<string>().HasMaxLength(20);

        // One report per browser and ask.
        builder.HasIndex(report => new { report.AskId, report.VoterHash }).IsUnique();

        builder.HasOne<Ask>().WithMany().HasForeignKey(report => report.AskId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AskConsentRecordConfiguration : IEntityTypeConfiguration<AskConsentRecord>
{
    public void Configure(EntityTypeBuilder<AskConsentRecord> builder)
    {
        builder.ToTable("consents", "askfriend");

        builder.HasKey(consent => new { consent.UserId, consent.Version }).HasName("pk_askfriend_consents");
        builder.Property(consent => consent.Version).HasMaxLength(AskConsentRecord.MaxVersionLength);
        builder.Property(consent => consent.Text).HasMaxLength(AskConsentRecord.MaxTextLength);

        builder.HasOne<User>().WithMany().HasForeignKey(consent => consent.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
