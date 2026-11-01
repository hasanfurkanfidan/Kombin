using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Persistence;

internal sealed class KombinDbContext(DbContextOptions<KombinDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KombinDbContext).Assembly);
}
