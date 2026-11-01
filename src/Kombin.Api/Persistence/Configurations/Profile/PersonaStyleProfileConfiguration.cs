using System.Text.Json;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class PersonaStyleProfileConfiguration : IEntityTypeConfiguration<PersonaStyleProfile>
{
    public void Configure(EntityTypeBuilder<PersonaStyleProfile> builder)
    {
        builder.ToTable("persona_style_profiles", "profile");

        builder.HasKey(profile => profile.PersonaId);
        builder.Property(profile => profile.PersonaId).ValueGeneratedNever();

        AsJsonb(builder.Property(profile => profile.ColorWeights));
        AsJsonb(builder.Property(profile => profile.FamilyWeights));
        AsJsonb(builder.Property(profile => profile.FitWeights));
        AsJsonb(builder.Property(profile => profile.SeasonWeights));
        builder.Property(profile => profile.FormalityMean).HasPrecision(3, 2);

        builder.HasOne<Persona>()
            .WithOne()
            .HasForeignKey<PersonaStyleProfile>(profile => profile.PersonaId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// A weight map is stored as a jsonb document. The conversion is explicit because Npgsql only writes arbitrary CLR types to jsonb
    /// after an opt-in for dynamic JSON on the data source, and a plain string needs none.
    /// </summary>
    private static void AsJsonb(PropertyBuilder<Dictionary<string, double>> property) =>
        property
            .HasColumnType("jsonb")
            .HasConversion(
                weights => JsonSerializer.Serialize(weights, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<Dictionary<string, double>>(json, (JsonSerializerOptions?)null) ?? new Dictionary<string, double>(),
                new ValueComparer<Dictionary<string, double>>(
                    (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.Count == right.Count && !left.Except(right).Any()),
                    weights => weights.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                    weights => weights.ToDictionary(pair => pair.Key, pair => pair.Value)));
}
