using static Kombin.Api.Tests.FeatureBoundaryTests;

namespace Kombin.Api.Tests;

/// <summary>
/// What one feature may use of another, frozen when <see cref="FeatureBoundaryTests"/> was written (task B7: nothing was moved to produce it, so
/// 141 references in 37 feature pairs: 90 Seam, 51 Legacy; task L2 moved the Istanbul zone and the Hangfire flag to Common, leaving 129 in 33
/// pairs: 90 Seam, 39 Legacy; task L3 added the league's two seams: 132 in 36 pairs (the list held 34 pairs before, not 33), 93 Seam, 39 Legacy). <see cref="Kind.Seam"/>: fine as it is. <see cref="Kind.Legacy"/>: accepted for now; the reason says where the
/// code belongs. The list only shrinks: do not add a line to make the test pass (CLAUDE.md, "Feature boundaries"). Grouped by the using feature.
/// </summary>
internal static class FeatureBoundaryAllowList
{
    private const string UseCases = "The founder's panel calls the same use-case code as the endpoints; it never bypasses a domain rule (Admin rules).";

    private const string VisionClient = "The vision-service client is infrastructure like Common/Storage; it belongs in Common/Vision.";

    private const string EnumParsing = "A generic strict enum parser for payloads; it belongs in Common (Common/Validation).";

    private const string PersonaConvention = "The persona convention: the feature scores products against the caller's persona with Profile's pure scorer and style profile (Profile rules).";

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // -- Admin ------------------------------------------------------------------------------------------------------------------
        new("Admin", "Catalog", Kind.Seam, UseCases, "Jobs.ProductEmbeddingJob", "Embeddings.EmbeddingRunLock"),
        new("Admin", "Identity", Kind.Seam, UseCases, "AccountAccess", "AccountDeletion", "AccountDeletionRules", "AccountPurger", "PurgeStatus"),
        new("Admin", "Social", Kind.Seam, UseCases, "Review.PostReviewer", "Review.ReviewEndpoint", "ReviewDecision", "Creators.CreatorInvites", "Creators.PutCreatorRequest"),
        new("Admin", "Social", Kind.Seam, "The panel shows the numbers and limits the feature's own rules use, read from the one place they are defined.", "ReportRules", "SocialLimits", "CreatorEarnings", "CreatorOptions"),
        new("Admin", "Wardrobe", Kind.Legacy, "The dashboard health strip asks the vision service. " + VisionClient, "IVisionClient", "VisionHealth"),

        // -- Analytics --------------------------------------------------------------------------------------------------------------
        new("Analytics", "Catalog", Kind.Legacy, EnumParsing + " ProductSort is an enum whose names the event allow-list should spell out itself.", "EnumNames", "ListProducts.ProductSort"),
        new("Analytics", "Lens", Kind.Legacy, "The event allow-list reuses Lens's CategoryFamily names; it should list its own, so a Lens change cannot silently change an event contract.", "CategoryFamily"),

        // -- Catalog ----------------------------------------------------------------------------------------------------------------
        new("Catalog", "Engagement", Kind.Seam, "Documented: alert evaluation lives in the catalog upsert path and the offer sweep (Engagement rules).", "AlertEvaluator", "EvaluationReason"),
        new("Catalog", "Profile", Kind.Seam, "Documented: catalog list and detail take personaId and add matchScore and size hints (Profile rules). Catalog and Profile also depend on each other, which is the part to untangle.", "PersonaScoring", "ProductTraits", "ProfileErrors", "SizeHintResponse", "SizeHints"),
        new("Catalog", "Wardrobe", Kind.Legacy, "The embedding job uses the vision client and the 8 MB image limit. " + VisionClient, "IVisionClient", "WardrobeLimits"),

        // -- Engagement -------------------------------------------------------------------------------------------------------------
        new("Engagement", "Catalog", Kind.Seam, "Saved products and alerts repeat the catalog's list item and effective-price rule, and answer its errors (saved items carry every catalog list item field).", "BrandRef", "CategoryRef", "SellerRef", "ProductListItem", "CatalogErrors", "GetProduct.GetProductEndpoint", "ListProducts.ListProductsEndpoint"),
        new("Engagement", "Catalog", Kind.Legacy, EnumParsing, "EnumNames"),
        new("Engagement", "Social", Kind.Seam, "Documented: /go attributes a click to a post and its creator (Social rules, CreatorAttribution).", "CreatorAttribution", "CreatorOptions"),

        // -- Identity ---------------------------------------------------------------------------------------------------------------
        new("Identity", "Analytics", Kind.Seam, "Documented: IAnalyticsIdentityLinker is the only coupling between Identity and Analytics (Analytics rules).", "IAnalyticsIdentityLinker"),
        new("Identity", "Notification", Kind.Seam, "Documented: other features reach devices only through IDeviceDeactivator (Notification rules).", "IDeviceDeactivator"),

        // -- League -----------------------------------------------------------------------------------------------------------------
        new("League", "Recommendation", Kind.Seam, "Documented: engine outfits fill a small pool; they come from the outfit engine, never a copy (Recommendation rules, CatalogOutfitSampler; League rules).", "CatalogOutfitSampler", "SampledOutfit"),
        new("League", "Wardrobe", Kind.Seam, "Documented: league entries show wardrobe pieces to strangers only through the image safety filter (Wardrobe rules, IsShareable; League rules).", "WardrobeModerator"),

        // -- Lens -------------------------------------------------------------------------------------------------------------------
        new("Lens", "Catalog", Kind.Seam, "Results carry the catalog's product list item and seller (Visual search rules).", "ProductListItem", "SellerRef"),
        new("Lens", "Engagement", Kind.Legacy, "ProductSummaries builds catalog list items; it lives in Engagement by history and belongs in Catalog.", "ProductSummaries"),
        new("Lens", "Recommendation", Kind.Legacy, "OutfitSources.CheapestOffersAsync (cheapest sellable offer by effective price) is offer pricing; it belongs in Catalog.", "OutfitSources"),
        new("Lens", "Wardrobe", Kind.Seam, "Wardrobe matches show the user's own pieces in the wardrobe's response shape (Visual search rules).", "WardrobeItemResponses", "WardrobeItemSummary"),
        new("Lens", "Wardrobe", Kind.Legacy, VisionClient + " WardrobeLimits is the shared image-size limit; it belongs next to the storage code.", "IVisionClient", "VisionDetection", "WardrobeLimits"),

        // -- Notification -----------------------------------------------------------------------------------------------------------
        new("Notification", "Catalog", Kind.Legacy, EnumParsing, "EnumNames"),
        new("Notification", "Profile", Kind.Seam, "Documented: weekly-matches scores new products for the active persona (Notification rules).", "MatchScorer", "ProductTraits", "StyleProfiles", "UserProfiles"),

        // -- Profile ----------------------------------------------------------------------------------------------------------------
        new("Profile", "Catalog", Kind.Seam, "Size entries and size questions show the catalog's brand and seller and answer its errors.", "BrandRef", "SellerRef", "CatalogErrors"),
        new("Profile", "Catalog", Kind.Legacy, EnumParsing, "EnumNames"),

        // -- Recommendation ---------------------------------------------------------------------------------------------------------
        new("Recommendation", "Catalog", Kind.Seam, "Gap pieces carry their cheapest offer by the product page's effective-price order, and sellers in the catalog's shape (Recommendation rules).", "GetProduct.GetProductEndpoint", "SellerRef"),
        new("Recommendation", "Catalog", Kind.Legacy, EnumParsing, "EnumNames"),
        new("Recommendation", "Notification", Kind.Legacy, "Copy holds the Turkish money formatting (2.149 ₺) the outfit copy reuses; it belongs in Common/Text next to CopyGlossary.", "Copy"),
        new("Recommendation", "Profile", Kind.Seam, PersonaConvention + " Budget outfits also read the size map (Budget rules).", "PersonaScoring", "ProductFamilies", "ProductTraits", "ProfileErrors", "StyleProfiles", "UserProfiles", "SizeGroups", "SizeInference", "SizeVote"),
        new("Recommendation", "Wardrobe", Kind.Seam, "Documented: worn goes through WearSummaries.RecordAsync, and outfits show wardrobe pieces in the wardrobe's shape (Wardrobe rules).", "RecordWear.WearSummaries", "WearRules", "WardrobeItemResponses"),
        new("Recommendation", "Wardrobe", Kind.Legacy, "Gap analysis and the outfit counts call each other (Wardrobe and Recommendation form a cycle): the pure gap rules belong with the outfit engine, or the counts are passed in.", "GapAnalysis", "GapPersona", "GapPiece", "WardrobeGap"),

        // -- Social -----------------------------------------------------------------------------------------------------------------
        new("Social", "Catalog", Kind.Seam, "Post tags carry the catalog's seller shape.", "SellerRef"),
        new("Social", "Catalog", Kind.Legacy, EnumParsing + " UpsertOfferItemValidator is only used for its public-URL check, which belongs in Common/Validation.", "EnumNames", "UpsertOffers.UpsertOfferItemValidator"),
        new("Social", "Notification", Kind.Legacy, "Copy: Turkish money formatting, see Recommendation.", "Copy"),
        new("Social", "Profile", Kind.Seam, "Documented: the feed filters and scores by the viewer's persona (Social rules).", "PersonaScoring", "ProductTraits", "ProfileErrors", "UserProfiles"),
        new("Social", "Recommendation", Kind.Seam, "Documented: \"Dolabımla dene\" is the how-to-wear engine with a second caller, never a copy (Recommendation rules).", "HowToWear", "HowToWearAnchor", "OutfitResponse", "OutfitResponses", "OutfitSlots", "OutfitStore", "PieceSource"),
        new("Social", "Recommendation", Kind.Legacy, "Offer pricing (OutfitSources) belongs in Catalog; TurkishGrammar is a Turkish text helper for Common/Text; DailyOutfitQueryValidator is reused for its rules and should be shared validation.", "OutfitSources", "Narration.TurkishGrammar", "GetDaily.DailyOutfitQueryValidator"),
        new("Social", "Wardrobe", Kind.Seam, "Outfit posts show wardrobe pieces in the wardrobe's shape and reuse its image safety filter (Wardrobe rules, IsShareable).", "WardrobeItemResponses", "WardrobeModerator"),
        new("Social", "Wardrobe", Kind.Legacy, "Post processing moderates and detects through the vision client. " + VisionClient, "IVisionClient", "VisionDetection", "VisionModeration"),

        // -- Wardrobe ---------------------------------------------------------------------------------------------------------------
        new("Wardrobe", "Catalog", Kind.Seam, "Pieces added from the catalog and suggested products use the catalog's brand, seller and list item and answer its errors.", "BrandRef", "SellerRef", "ProductListItem", "CatalogErrors"),
        new("Wardrobe", "Catalog", Kind.Legacy, EnumParsing, "EnumNames"),
        new("Wardrobe", "Engagement", Kind.Legacy, "ProductSummaries builds catalog list items; it belongs in Catalog.", "ProductSummaries"),
        new("Wardrobe", "Notification", Kind.Legacy, "Copy: Turkish money formatting. WeeklyMatchSelection is a pure match-score selection that belongs with Profile's scorer.", "Copy", "WeeklyMatchSelection"),
        new("Wardrobe", "Profile", Kind.Seam, PersonaConvention, "PersonaScoring", "ProductTraits", "ProfileErrors", "StyleProfiles", "UserProfiles"),
        new("Wardrobe", "Recommendation", Kind.Seam, "Documented: pairings, counts, unlocks and possible outfits come from the outfit engine (Wardrobe rules).", "OutfitCounts", "OutfitPairs", "OutfitPiece", "PairGroup", "PairReason", "PairUnlock", "PersonaChoice", "RecommendationErrors"),
        new("Wardrobe", "Recommendation", Kind.Legacy, "The pairings and summary pages reach into the engine's rule tables and loaders; the engine should hand back a finished result (and Wardrobe and Recommendation form a cycle). WeatherRules.SeasonOf is a season-of-date helper for Common/Time.", "ColorRules", "FormalityRules", "CatalogQuery", "OutfitSources", "WeatherRules"),
    ];
}
