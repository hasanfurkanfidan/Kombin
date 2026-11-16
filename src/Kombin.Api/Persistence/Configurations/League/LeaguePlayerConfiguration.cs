using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.League;

internal sealed class LeaguePlayerConfiguration : IEntityTypeConfiguration<LeaguePlayer>
{
    public void Configure(EntityTypeBuilder<LeaguePlayer> builder)
    {
        builder.ToTable("players", "league", table =>
            table.HasCheckConstraint("ck_players_counters", "counted_votes >= 0 AND idle_weeks >= 0"));

        builder.HasKey(player => player.UserId);
        builder.Property(player => player.Tier).HasConversion<string>().HasMaxLength(20);
        builder.Property(player => player.ConsentVersion).HasMaxLength(LeagueConsentRecord.MaxVersionLength);
        builder.Ignore(player => player.IsJoined);

        builder.HasOne<User>().WithMany().HasForeignKey(player => player.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeagueConsentRecordConfiguration : IEntityTypeConfiguration<LeagueConsentRecord>
{
    public void Configure(EntityTypeBuilder<LeagueConsentRecord> builder)
    {
        builder.ToTable("consents", "league");

        builder.HasKey(consent => consent.Id);
        builder.Property(consent => consent.Id).UseIdentityAlwaysColumn();
        builder.Property(consent => consent.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(consent => consent.Version).HasMaxLength(LeagueConsentRecord.MaxVersionLength);
        builder.Property(consent => consent.Text).HasMaxLength(LeagueConsentRecord.MaxTextLength);

        // A player's history, oldest first.
        builder.HasIndex(consent => new { consent.UserId, consent.At });

        builder.HasOne<User>().WithMany().HasForeignKey(consent => consent.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
