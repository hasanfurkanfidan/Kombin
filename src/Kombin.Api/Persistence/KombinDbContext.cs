using Kombin.Api.Persistence.Entities.Admin;
using Kombin.Api.Persistence.Entities.Analytics;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Persistence;

internal sealed class KombinDbContext(DbContextOptions<KombinDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<Seller> Sellers => Set<Seller>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    public DbSet<ProductEmbedding> ProductEmbeddings => Set<ProductEmbedding>();

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

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<Entities.Notification.Notification> Notifications => Set<Entities.Notification.Notification>();

    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();

    public DbSet<WeeklyMetric> WeeklyMetrics => Set<WeeklyMetric>();

    public DbSet<WardrobeItem> WardrobeItems => Set<WardrobeItem>();

    public DbSet<WardrobeWear> WardrobeWears => Set<WardrobeWear>();

    public DbSet<Outfit> Outfits => Set<Outfit>();

    public DbSet<OutfitItem> OutfitItems => Set<OutfitItem>();

    public DbSet<OutfitFeedback> OutfitFeedback => Set<OutfitFeedback>();

    public DbSet<OutfitNarration> OutfitNarrations => Set<OutfitNarration>();

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<PostTag> PostTags => Set<PostTag>();

    public DbSet<Follow> Follows => Set<Follow>();

    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    public DbSet<PostLike> PostLikes => Set<PostLike>();

    public DbSet<PostReport> PostReports => Set<PostReport>();

    public DbSet<PostingConsentRecord> PostingConsents => Set<PostingConsentRecord>();

    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();

    public DbSet<ReferralEvent> ReferralEvents => Set<ReferralEvent>();

    public DbSet<AdminAuditEntry> AdminAuditLog => Set<AdminAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector, for the wardrobe and catalog embeddings (visual search).
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KombinDbContext).Assembly);
    }
}
