using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Identity;

internal sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("external_logins", "identity", table =>
            table.HasCheckConstraint("ck_external_logins_provider", "provider IN ('google', 'apple')"));

        builder.HasKey(login => login.Id);
        builder.Property(login => login.Id).ValueGeneratedNever();

        builder.Property(login => login.Provider).HasColumnType("text").IsRequired();
        builder.Property(login => login.Subject).HasColumnType("text").IsRequired();
        builder.Property(login => login.EmailAtLink).HasColumnType("text");

        // What makes two simultaneous first sign-ins with the same token end up as one user.
        builder.HasIndex(login => new { login.Provider, login.Subject }).IsUnique();
        builder.HasIndex(login => login.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(login => login.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
