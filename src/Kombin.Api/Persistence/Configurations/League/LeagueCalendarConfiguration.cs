using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.League;

internal sealed class LeagueDayConfiguration : IEntityTypeConfiguration<LeagueDay>
{
    public void Configure(EntityTypeBuilder<LeagueDay> builder)
    {
        builder.ToTable("days", "league");

        builder.HasKey(day => day.Day);
        builder.Property(day => day.CloseMilliseconds).HasColumnName("close_ms");
    }
}

internal sealed class LeagueWeekConfiguration : IEntityTypeConfiguration<LeagueWeek>
{
    public void Configure(EntityTypeBuilder<LeagueWeek> builder)
    {
        builder.ToTable("weeks", "league", table =>
            table.HasCheckConstraint("ck_weeks_monday", "extract(isodow FROM week_start) = 1"));

        builder.HasKey(week => week.WeekStart);
    }
}

internal sealed class LeagueStateConfiguration : IEntityTypeConfiguration<LeagueState>
{
    public void Configure(EntityTypeBuilder<LeagueState> builder)
    {
        builder.ToTable("state", "league");

        builder.HasKey(state => state.Key);
        builder.Property(state => state.Key).HasMaxLength(LeagueState.MaxKeyLength);
        builder.Property(state => state.Value).HasMaxLength(LeagueState.MaxValueLength);
    }
}
