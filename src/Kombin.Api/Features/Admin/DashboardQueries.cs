namespace Kombin.Api.Features.Admin;

/// <summary>
/// One number of the dashboard and the SQL it comes from. A windowed query counts what happened since <c>@since</c> (the dashboard runs it
/// twice: since the start of today in Istanbul and since 7 days ago); the others describe the state right now. <see cref="Meaning"/> says
/// in a sentence what the number counts, so the founder learns what he is looking at; the page prints both next to the number.
/// </summary>
internal sealed record DashboardMetric(string Key, string Title, string Meaning, string Sql, bool Windowed);

/// <summary>A count per label (<c>SELECT label, count</c>), windowed like a <see cref="DashboardMetric"/>.</summary>
internal sealed record DashboardBreakdown(string Key, string Title, string Meaning, string Sql);

/// <summary>
/// Every query the admin dashboard runs, in one place so a test can run them all against a real database (a typo in a table or column
/// name is a test failure, not a blank dashboard). They read the same tables the API writes; nothing here is a copy of data.
/// </summary>
internal static class DashboardQueries
{
    public static readonly IReadOnlyList<DashboardMetric> Metrics =
    [
        new("new_users", "New users", "Accounts created (register or first Google/Apple sign-in).",
            "SELECT count(*) FROM identity.users WHERE created_at >= @since", Windowed: true),
        new("posts_created", "Posts created", "Posts started, in any status (a photo upload creates one).",
            "SELECT count(*) FROM social.posts WHERE created_at >= @since", Windowed: true),
        new("posts_live", "Posts gone live", "Posts that went live in the period and are still live.",
            "SELECT count(*) FROM social.posts WHERE status = 'Live' AND published_at >= @since", Windowed: true),
        new("clicks", "Buy clicks", "Clicks through /go, the affiliate redirect (the revenue path).",
            "SELECT count(*) FROM engagement.clicks WHERE clicked_at >= @since", Windowed: true),
        new("alerts_triggered", "Price alerts triggered", "Alerts that fired (price drop, back in stock, any change); the notification job turns them into pushes.",
            "SELECT count(*) FROM engagement.alert_triggers WHERE triggered_at >= @since", Windowed: true),
        new("lens_searches", "Lens searches", "Photo searches reported by the app (the lens_search analytics event; the photo itself is never stored).",
            "SELECT count(*) FROM analytics.events WHERE name = 'lens_search' AND received_at >= @since", Windowed: true),
        new("sellers_reporting", "Sellers with a fresh offer", "Active sellers whose workers saw at least one offer in the period: zero means a worker is not running.",
            "SELECT count(DISTINCT o.seller_id) FROM catalog.offers o JOIN catalog.sellers s ON s.id = o.seller_id WHERE s.is_active AND o.last_seen_at >= @since", Windowed: true),
        new("sellers_active", "Active sellers", "Sellers switched on, whether or not a worker has reported lately.",
            "SELECT count(*) FROM catalog.sellers WHERE is_active", Windowed: false),
        new("products_active", "Active products", "Products with at least one active offer.",
            "SELECT count(*) FROM catalog.products WHERE is_active", Windowed: false),
        new("products_embedded", "Products with an embedding", "Active products visual search can find (a vector is stored and ready).",
            "SELECT count(*) FROM catalog.products p WHERE p.is_active AND EXISTS (SELECT 1 FROM catalog.product_embeddings e WHERE e.product_id = p.id AND e.status = 'Ready')", Windowed: false),
        new("products_unembedded", "Products without an embedding", "Active products visual search cannot find yet: not tried, failed or with an unusable image.",
            "SELECT count(*) FROM catalog.products p WHERE p.is_active AND NOT EXISTS (SELECT 1 FROM catalog.product_embeddings e WHERE e.product_id = p.id AND e.status = 'Ready')", Windowed: false),
    ];

    public static readonly IReadOnlyList<DashboardBreakdown> Breakdowns =
    [
        new("clicks_by_source", "Buy clicks by source", "Which screen the click came from (feed, lens, outfit, product_detail, ...).",
            "SELECT source, count(*) FROM engagement.clicks WHERE clicked_at >= @since GROUP BY source ORDER BY count(*) DESC, source"),
    ];

    public const string DatabaseSizeSql = "SELECT pg_database_size(current_database())";

    public static DashboardMetric Metric(string key) => Metrics.Single(metric => metric.Key == key);
}
