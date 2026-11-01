using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Profile;

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

    public DbSet<SavedProduct> SavedProducts => Set<SavedProduct>();

    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();

    public DbSet<AlertTrigger> AlertTriggers => Set<AlertTrigger>();

    public DbSet<Click> Clicks => Set<Click>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<Persona> Personas => Set<Persona>();

    public DbSet<StyleCard> StyleCards => Set<StyleCard>();

    public DbSet<StyleSwipe> StyleSwipes => Set<StyleSwipe>();

    public DbSet<PersonaStyleProfile> PersonaStyleProfiles => Set<PersonaStyleProfile>();

    public DbSet<SizeEntry> SizeEntries => Set<SizeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KombinDbContext).Assembly);
}
