using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.League;

internal sealed class LeagueDailyScoreConfiguration : IEntityTypeConfiguration<LeagueDailyScore>
{
    public void Configure(EntityTypeBuilder<LeagueDailyScore> builder)
    {
        builder.ToTable("daily_scores", "league", table =>
        {
            table.HasCheckConstraint("ck_daily_scores_parts",
                "vote_points BETWEEN 0 AND 70 AND voting_points IN (0, 15) AND worn_points IN (0, 10) AND piece_points IN (0, 5)");
            table.HasCheckConstraint("ck_daily_scores_total", "total = vote_points + voting_points + worn_points + piece_points");
        });

        // The key also serves the week sums (a player's days of a week).
        builder.HasKey(score => new { score.UserId, score.Day });
        builder.HasIndex(score => score.Day);

        builder.HasOne<User>().WithMany().HasForeignKey(score => score.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LeagueEntry>().WithMany().HasForeignKey(score => score.EntryId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class LeagueGroupConfiguration : IEntityTypeConfiguration<LeagueGroup>
{
    public void Configure(EntityTypeBuilder<LeagueGroup> builder)
    {
        builder.ToTable("groups", "league");

        builder.HasKey(group => group.Id);
        builder.Property(group => group.Id).ValueGeneratedNever();

        // A text[] of names, like every enum of the schema.
        builder.PrimitiveCollection(group => group.Tiers).ElementType().HasConversion<string>();

        builder.HasIndex(group => group.WeekStart);

        builder.HasOne<LeagueWeek>().WithMany().HasForeignKey(group => group.WeekStart).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueGroupMemberConfiguration : IEntityTypeConfiguration<LeagueGroupMember>
{
    public void Configure(EntityTypeBuilder<LeagueGroupMember> builder)
    {
        builder.ToTable("group_members", "league");

        // One group per player per week.
        builder.HasKey(member => new { member.WeekStart, member.UserId });
        builder.Property(member => member.Alias).HasMaxLength(LeagueGroupMember.MaxAliasLength);
        builder.Property(member => member.Tier).HasConversion<string>().HasMaxLength(20);
        builder.Property(member => member.Outcome).HasConversion<string>().HasMaxLength(20);

        // An alias is unique within its group; the index also lists a group's members.
        builder.HasIndex(member => new { member.GroupId, member.Alias }).IsUnique();

        // A player's history across weeks (and the alias of the previous week).
        builder.HasIndex(member => new { member.UserId, member.WeekStart });

        builder.HasOne<LeagueGroup>().WithMany().HasForeignKey(member => member.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(member => member.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueStyleScoreConfiguration : IEntityTypeConfiguration<LeagueStyleScore>
{
    public void Configure(EntityTypeBuilder<LeagueStyleScore> builder)
    {
        builder.ToTable("style_scores", "league", table =>
            table.HasCheckConstraint("ck_style_scores_score", "score IS NULL OR score BETWEEN 0 AND 100"));

        builder.HasKey(score => score.UserId);
        builder.Property(score => score.MeanPercentile).HasColumnName("mean_p");
        builder.Property(score => score.SdPercentile).HasColumnName("sd_p");
        builder.Property(score => score.RevokedReason).HasMaxLength(LeagueStyleScore.MaxRevokedReasonLength);

        builder.HasOne<User>().WithMany().HasForeignKey(score => score.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueEnginePreferenceConfiguration : IEntityTypeConfiguration<LeagueEnginePreference>
{
    public void Configure(EntityTypeBuilder<LeagueEnginePreference> builder)
    {
        builder.ToTable("engine_preferences", "league");

        builder.HasKey(preference => preference.Id);
        builder.Property(preference => preference.Id).UseIdentityAlwaysColumn();
        builder.Property(preference => preference.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(preference => preference.Occasion).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(preference => preference.Day);
    }
}
