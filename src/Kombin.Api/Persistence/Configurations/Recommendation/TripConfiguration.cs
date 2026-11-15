using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Recommendation;

internal sealed class TripConfiguration : IEntityTypeConfiguration<Trip>
{
    public void Configure(EntityTypeBuilder<Trip> builder)
    {
        builder.ToTable("trips", "recommendation", table =>
            table.HasCheckConstraint("ck_trips_dates", "end_date >= start_date"));

        builder.HasKey(trip => trip.Id);
        builder.Property(trip => trip.Id).ValueGeneratedNever();

        builder.Property(trip => trip.Name).HasMaxLength(Trip.MaxNameLength);
        builder.Property(trip => trip.City).HasMaxLength(Trip.MaxCityLength);
        builder.Property(trip => trip.RequestJson).HasColumnType("jsonb");
        builder.Property(trip => trip.PlanJson).HasColumnType("jsonb");

        // GET /me/trips, newest first.
        builder.HasIndex(trip => new { trip.UserId, trip.CreatedAt });

        // The account's deletion takes the trips with it.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(trip => trip.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
