using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

/// <summary>The OpenAPI document is generated from the endpoints and their records; these checks keep the new surface from silently missing.</summary>
public sealed class OpenApiTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> DocumentAsync()
    {
        using var client = factory.CreateClient();

        return await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);
    }

    [Fact]
    public async Task TheProductListItemDocumentsTheCheapestSeller()
    {
        var document = await DocumentAsync();
        var paged = document.GetProperty("paths").GetProperty("/api/v1/catalog/products").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText();
        Assert.Contains("ProductListItem", paged, StringComparison.Ordinal);

        var item = document.GetProperty("components").GetProperty("schemas").GetProperty("ProductListItem").GetProperty("properties");
        Assert.True(item.TryGetProperty("cheapestSeller", out var cheapestSeller));
        Assert.Contains("SellerRef", cheapestSeller.GetRawText(), StringComparison.Ordinal);

        var seller = document.GetProperty("components").GetProperty("schemas").GetProperty("SellerRef").GetProperty("properties");
        Assert.True(seller.TryGetProperty("name", out _));
        Assert.True(seller.TryGetProperty("slug", out _));
    }

    // The league is off by default, but its routes are mapped and documented so the app can be built against them.
    [Fact]
    public async Task TheLeagueRoutesAreDocumentedWithTheirShapes()
    {
        var document = await DocumentAsync();
        var paths = document.GetProperty("paths");

        Assert.Contains("LeagueMeResponse", paths.GetProperty("/api/v1/league/me").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("LeagueMeResponse", paths.GetProperty("/api/v1/league/join").GetProperty("post").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.True(paths.GetProperty("/api/v1/league/leave").GetProperty("post").GetProperty("responses").TryGetProperty("204", out _));

        Assert.True(paths.GetProperty("/api/v1/league/me/entry").GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
        Assert.Contains("NextPairResponse", paths.GetProperty("/api/v1/league/pairs/next").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("VoteResponse", paths.GetProperty("/api/v1/league/pairs/{pairId}/vote").GetProperty("post").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("ReportEntryResponse", paths.GetProperty("/api/v1/league/pairs/{pairId}/report").GetProperty("post").GetProperty("responses").GetProperty("202").GetRawText(), StringComparison.Ordinal);

        var schemas = document.GetProperty("components").GetProperty("schemas");
        string[] Properties(string schema) => [.. schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["consent", "consentRequired", "joined", "joinedAt", "tier", "today", "week"], Properties("LeagueMeResponse"));
        Assert.Equal(["closesAt", "countedVotes", "day", "entry", "limit", "servedPairs", "target"], Properties("LeagueTodayResponse"));
        Assert.Equal(["message", "pieces", "reason", "status"], Properties("LeagueTodayEntryResponse"));
        Assert.Equal(["closesAt", "countedToday", "limit", "pair", "reason", "servedToday", "target"], Properties("NextPairResponse"));
        Assert.Equal(["id", "left", "right", "sequence"], Properties("LeaguePairResponse"));
        Assert.Equal(["imageUrl", "slot"], Properties("LeaguePieceResponse"));
        Assert.Equal(["chosenGames", "chosenWinRate", "counted", "countedToday", "limit", "servedToday", "target"], Properties("VoteResponse"));
    }

    // Task L4 (league PR 3): the results of a closed day, and the operators' close.
    [Fact]
    public async Task TheLeagueResultsAndTheCloseAreDocumented()
    {
        var document = await DocumentAsync();
        var paths = document.GetProperty("paths");
        var schemas = document.GetProperty("components").GetProperty("schemas");
        string[] Properties(string schema) => [.. schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

        var results = paths.GetProperty("/api/v1/league/results").GetProperty("get");
        Assert.Contains("LeagueResultsResponse", results.GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains(results.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "day");
        Assert.Contains("LeagueResultsResponse", paths.GetProperty("/api/v1/league/results/latest").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("CloseDayResponse", paths.GetProperty("/api/v1/internal/league/days/{date}/close").GetProperty("post").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);

        Assert.Equal(["comparisons", "competed", "day", "entry", "message", "piecePointsAvailable", "points", "rank", "ranked", "rankedCount", "status", "week"], Properties("LeagueResultsResponse"));
        Assert.Equal(["pieces", "total", "votes", "voting", "worn"], Properties("LeaguePointsResponse"));
        Assert.Equal(["pieces"], Properties("LeagueResultEntryResponse"));
        Assert.Equal(["endsAt", "groupRank", "pointsToPromotion", "tier"], Properties("LeagueResultsWeekResponse"));
        Assert.Equal(["btConverged", "btIterations", "closeMs", "closedAt", "closedNow", "day", "engineCount", "pairCount", "rankedCount"], Properties("CloseDayResponse"));
        Assert.Equal(["Final", "UnderReview", "Removed"], schemas.GetProperty("LeagueResultStatus").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    // Task L5 (league PR 4): the weekly table, the review queue and the result push's preference; this completes the league contract.
    [Fact]
    public async Task TheLeagueTableAndTheReviewRoutesAreDocumented()
    {
        var document = await DocumentAsync();
        var paths = document.GetProperty("paths");
        var schemas = document.GetProperty("components").GetProperty("schemas");
        string[] Properties(string schema) => [.. schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

        Assert.Contains("LeagueTableResponse", paths.GetProperty("/api/v1/league/table").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("LeagueReviewItem", paths.GetProperty("/api/v1/internal/league/review-queue").GetProperty("get").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("ReviewEntryResponse", paths.GetProperty("/api/v1/internal/league/entries/{entryId}/review").GetProperty("post").GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);

        Assert.Equal(["endsAt", "promoteCount", "relegateCount", "rows", "size", "state", "tier", "tierDown", "tierUp", "todayIsFinal", "todayPoints", "weekStart"], Properties("LeagueTableResponse"));
        Assert.Equal(["alias", "isMe", "points", "rank"], Properties("LeagueTableRow"));
        Assert.Equal(["endsAt", "groupRank", "groupSize", "points", "pointsToPromotion", "state"], Properties("LeagueWeekResponse"));
        Assert.Equal(["countingReports", "day", "entryId", "pieces", "reports", "status", "underReview"], Properties("LeagueReviewItem"));
        Assert.Equal(["entryId", "resultChanged", "status"], Properties("ReviewEntryResponse"));
        Assert.Contains("leagueResults", Properties("PreferencesResponse"));
        Assert.Contains("leagueResults", Properties("UpdatePreferencesRequest"));
    }

    // Security review M5: the API documentation must say that "Followers" is not private (anyone can follow without approval).
    [Fact]
    public async Task PublishingSaysFollowersIsNotPrivate()
    {
        var document = await DocumentAsync();
        var operation = document.GetProperty("paths").GetProperty("/api/v1/posts/{id}/publish").GetProperty("put");

        Assert.Contains("Followers is NOT a privacy boundary", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    // Task L3-fix: an outfit dismissed after it was worn leaves the entry NotCompeting with NotWorn.
    [Fact]
    public async Task TheLeagueEntryReasonsAreDocumented()
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");

        Assert.Equal(
            ["RecentlyCompeted", "UnsafePiece", "IncompleteOutfit", "PieceUnavailable", "AgeRestricted", "NotWorn", null],
            schemas.GetProperty("LeagueEntryReason").GetProperty("enum").EnumerateArray().Select(reason => reason.GetString()));
    }

    [Fact]
    public async Task ThePairingsAreDocumentedWithTheirReasons()
    {
        var document = await DocumentAsync();
        var operation = document.GetProperty("paths").GetProperty("/api/v1/me/wardrobe/items/{id}/pairings").GetProperty("get");
        Assert.Contains("PairingsResponse", operation.GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);

        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            ["neutral", "tonal", "complementary", "analogous", "statement"],
            schemas.GetProperty("PairReason").GetProperty("enum").EnumerateArray().Select(reason => reason.GetString()));
        Assert.True(schemas.GetProperty("WardrobeItemSummary").GetProperty("properties").TryGetProperty("companionCount", out _));
    }

    [Fact]
    public async Task TheProfileUpdateDocumentsTheCityAndNotItsFlags()
    {
        var properties = (await DocumentAsync()).GetProperty("components").GetProperty("schemas").GetProperty("UpdateProfileRequest").GetProperty("properties");

        Assert.True(properties.TryGetProperty("city", out _));
        Assert.False(properties.TryGetProperty("citySet", out _));
    }

    // Task P3: "Manken üstünde gör" is off by default, but its routes are mapped and documented so the app can be built against them.
    [Fact]
    public async Task TheTryOnRoutesAreDocumentedWithTheirShapes()
    {
        var document = await DocumentAsync();
        var paths = document.GetProperty("paths");

        var get = paths.GetProperty("/api/v1/me/tryon").GetProperty("get");
        // [AsParameters] records are documented by property name (OutfitId), like the other query records; binding ignores the case.
        Assert.Equal(
            ["check", "outfitid", "productid", "withwardrobe"],
            get.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!.ToLowerInvariant()).Order(StringComparer.Ordinal));
        Assert.Contains("TryOnResponse", get.GetProperty("responses").GetProperty("200").GetRawText(), StringComparison.Ordinal);
        Assert.All(["400", "404", "429", "503"], status => Assert.True(get.GetProperty("responses").TryGetProperty(status, out _), status));
        Assert.True(paths.GetProperty("/api/v1/me/tryon/reports").GetProperty("post").GetProperty("responses").TryGetProperty("204", out _));

        var schemas = document.GetProperty("components").GetProperty("schemas");
        string[] Properties(string schema) => [.. schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
        string?[] Values(string schema) => [.. schemas.GetProperty(schema).GetProperty("enum").EnumerateArray().Select(value => value.GetString())];

        Assert.Equal(["defaultedMeasurements", "garments", "imageUrl", "reason", "renderer", "status"], Properties("TryOnResponse"));
        Assert.Equal(["fitLevel", "id"], Properties("TryOnGarmentResponse"));
        Assert.Equal(["garmentIds", "outfitId", "productId", "renderer"], Properties("ReportTryOnRequest"));
        Assert.Equal(["Ready", "Preparing", "Unavailable"], Values("TryOnStatus"));
        Assert.Contains("Tight", Values("TryOnFitLevel"));
        Assert.Contains("TrueToSize", Values("TryOnFitLevel"));
        Assert.Contains("Loose", Values("TryOnFitLevel"));
        Assert.Contains("BodyOutOfRange", Values("TryOnUnavailableReason"));
        Assert.Contains("RendererRefused", Values("TryOnUnavailableReason"));
    }

    [Fact]
    public async Task TheAnalyticsEndpointsAreDocumented()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        Assert.True(paths.GetProperty("/api/v1/events").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(paths.GetProperty("/api/v1/internal/analytics/weekly").TryGetProperty("get", out _));
    }
}
