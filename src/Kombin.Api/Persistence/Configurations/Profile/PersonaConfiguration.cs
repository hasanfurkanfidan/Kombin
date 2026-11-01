using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Profile;

internal sealed class PersonaConfiguration : IEntityTypeConfiguration<Persona>
{
    public void Configure(EntityTypeBuilder<Persona> builder)
    {
        builder.ToTable("personas", "profile", table =>
        {
            table.HasCheckConstraint("ck_personas_formality", "formality_min BETWEEN 1 AND 5 AND formality_max BETWEEN 1 AND 5 AND formality_min <= formality_max");
            table.HasCheckConstraint("ck_personas_budget", "budget_min IS NULL OR budget_max IS NULL OR budget_min <= budget_max");
        });

        builder.HasKey(persona => persona.Id);
        builder.Property(persona => persona.Id).ValueGeneratedNever();

        builder.Property(persona => persona.Name).HasMaxLength(40).IsRequired();
        builder.Property(persona => persona.Icon).HasMaxLength(40).IsRequired();
        builder.Property(persona => persona.BudgetMin).HasPrecision(12, 2);
        builder.Property(persona => persona.BudgetMax).HasPrecision(12, 2);
        builder.Property(persona => persona.PreferredColors).HasColumnType("text[]");
        builder.Property(persona => persona.AvoidedColors).HasColumnType("text[]");
        builder.Property(persona => persona.PreferredFits).HasColumnType("text[]");

        // Seasons is a flags enum stored as its integer value (smallint), like products.
        builder.Property(persona => persona.Seasons).HasConversion<short>();

        builder.HasIndex(persona => new { persona.UserId, persona.SortOrder });

        // A name may be reused once its persona is archived.
        builder.HasIndex(persona => new { persona.UserId, persona.Name })
            .IsUnique()
            .HasFilter("NOT is_archived");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(persona => persona.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
