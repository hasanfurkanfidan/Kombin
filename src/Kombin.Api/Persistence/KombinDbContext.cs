using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Persistence;

internal sealed class KombinDbContext(DbContextOptions<KombinDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<Seller> Sellers => Set<Seller>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KombinDbContext).Assembly);
}
