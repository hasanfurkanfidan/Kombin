using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.League;

internal sealed class LeaguePairConfiguration : IEntityTypeConfiguration<LeaguePair>
{
    public void Configure(EntityTypeBuilder<LeaguePair> builder)
    {
        builder.ToTable("pairs", "league", table =>
        {
            table.HasCheckConstraint("ck_pairs_two_entries", "left_entry_id <> right_entry_id AND low_entry_id < high_entry_id");
            table.HasCheckConstraint("ck_pairs_answer", "(answered_at IS NULL) = (choice IS NULL)");
            table.HasCheckConstraint("ck_pairs_weight", "weight >= 0 AND weight <= 1 AND (counted OR weight = 0)");
            table.HasCheckConstraint("ck_pairs_sequence", "sequence >= 1");
        });

        builder.HasKey(pair => pair.Id);
        builder.Property(pair => pair.Id).ValueGeneratedNever();
        builder.Property(pair => pair.MatchLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(pair => pair.Choice).HasConversion<string>().HasMaxLength(20);
        builder.Property(pair => pair.ClientDecisionMilliseconds).HasColumnName("client_decision_ms");

        // A pair is never shown twice to the same player, so a player votes once per pair.
        builder.HasIndex(pair => new { pair.VoterId, pair.Day, pair.LowEntryId, pair.HighEntryId }).IsUnique();

        // The daily cap cannot be raced: the n-th pair of a voter's day exists once.
        builder.HasIndex(pair => new { pair.VoterId, pair.Day, pair.Sequence }).IsUnique();

        // At most one open pair per voter: asking again returns it instead of re-rolling.
        builder.HasIndex(pair => new { pair.VoterId, pair.Day }).IsUnique().HasFilter("answered_at IS NULL").HasDatabaseName("ix_pairs_open");

        // The day close reads every counted vote of a day.
        builder.HasIndex(pair => pair.Day).IncludeProperties(pair => new { pair.LeftEntryId, pair.RightEntryId, pair.Choice, pair.Weight });

        builder.HasIndex(pair => pair.LeftEntryId);
        builder.HasIndex(pair => pair.RightEntryId);

        builder.HasOne<User>().WithMany().HasForeignKey(pair => pair.VoterId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LeagueEntry>().WithMany().HasForeignKey(pair => pair.LeftEntryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LeagueEntry>().WithMany().HasForeignKey(pair => pair.RightEntryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueReportConfiguration : IEntityTypeConfiguration<LeagueReport>
{
    public void Configure(EntityTypeBuilder<LeagueReport> builder)
    {
        builder.ToTable("reports", "league");

        // One report per player and outfit. Named explicitly: social.reports owns the default names.
        builder.HasKey(report => new { report.EntryId, report.ReporterId }).HasName("pk_league_reports");
        builder.Property(report => report.Reason).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(report => report.ReporterId).HasDatabaseName("ix_league_reports_reporter_id");

        builder.HasOne<LeagueEntry>().WithMany().HasForeignKey(report => report.EntryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(report => report.ReporterId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueVoterDayConfiguration : IEntityTypeConfiguration<LeagueVoterDay>
{
    public void Configure(EntityTypeBuilder<LeagueVoterDay> builder)
    {
        builder.ToTable("voter_days", "league", table =>
            table.HasCheckConstraint("ck_voter_days_counts", "served >= 0 AND counted >= 0 AND counted <= served"));

        // One row per voter and day; the close reads a day's rows, the purge deletes old days.
        builder.HasKey(row => new { row.UserId, row.Day });
        builder.HasIndex(row => row.Day);

        builder.HasOne<User>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
