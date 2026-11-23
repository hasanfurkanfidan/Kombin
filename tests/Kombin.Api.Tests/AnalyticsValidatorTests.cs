using System.Text.Json;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Analytics.RecordEvents;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence.Entities.AskFriend;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Tests;

/// <summary>
/// Two layers: the request validator, whose failures are a 400 for the whole body, and <see cref="AnalyticsEventCatalog.Reject"/>, whose
/// verdict costs only the one event (<c>unknown_event</c>, <c>invalid_properties</c>).
/// </summary>
public class AnalyticsValidatorTests
{
    private const string ProductId = "3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11";

    private readonly RecordEventsValidator _validator = new();

    private static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static string? Reject(string? name, string? properties = null) =>
        AnalyticsEventCatalog.Reject(name, properties is null ? null : Props(properties));

    private static IEnumerable<string> BadKeys(string name, string json) =>
        AnalyticsEventCatalog.Check(name, Props(json)).Select(problem => problem.Key);

    private static EventItem Event(string name = "app_open", string? properties = null, string? anonymousId = "anon-1") =>
        new(name, properties is null ? null : Props(properties), "session-1", "Ios", "1.0.0", DateTimeOffset.UtcNow, anonymousId);

    private static RecordEventsRequest Batch(params EventItem[] events) => new([.. events]);

    [Fact]
    public void TheLeagueResultStatusesAreTheEnumsNames()
    {
        // The catalog spells the statuses out (Analytics does not name the league's types); this keeps the two lists equal.
        foreach (var status in Enum.GetNames<Kombin.Api.Features.League.GetResults.LeagueResultStatus>())
        {
            Assert.Null(Reject("league_results_view", $$"""{"status":"{{status}}"}"""));
        }

        Assert.Equal(3, Enum.GetNames<Kombin.Api.Features.League.GetResults.LeagueResultStatus>().Length);
    }

    [Fact]
    public void TheAskViewStatusesAreTheEnumsNames()
    {
        // The catalog spells the statuses out (Analytics does not name the feature's types); this keeps the two lists equal.
        foreach (var status in Enum.GetNames<Kombin.Api.Features.AskFriend.AskViewState>())
        {
            Assert.Null(Reject("ask_view", $$"""{"askId":"{{ProductId}}","status":"{{status}}"}"""));
        }

        Assert.Equal(4, Enum.GetNames<Kombin.Api.Features.AskFriend.AskViewState>().Length);

        foreach (var kind in Enum.GetNames<AskKind>())
        {
            Assert.Null(Reject("ask_create", $$"""{"askId":"{{ProductId}}","kind":"{{kind}}"}"""));
        }
    }

    [Fact]
    public void TheBulkAddEventsAreCatalogued()
    {
        Assert.Null(Reject("bulk_add_start", """{"source":"camera"}"""));
        Assert.Null(Reject("bulk_add_start", """{"source":"library"}"""));
        Assert.Null(Reject("bulk_add_cancel", """{"stage":"Review"}"""));
        Assert.Null(Reject(
            "bulk_add_result",
            """{"status":"Ready","durationMs":5300,"pieces":3,"preTicked":2,"unseparated":0,"duplicateHints":1}"""));
        Assert.Null(Reject(
            "bulk_add_result",
            """{"status":"Refused","refusal":"closet","durationMs":2100,"pieces":0,"preTicked":0,"unseparated":0,"duplicateHints":0}"""));
        Assert.Null(Reject(
            "bulk_add_confirm",
            """{"shown":3,"ticked":3,"preTickedTicked":2,"preTickedLeft":0,"extraTicked":1,"categoryEdits":1,"colourEdits":0,"durationMs":41000}"""));
    }

    [Fact]
    public void TheBulkAddRefusalAndFailureCodesAreTheBackendsOwn()
    {
        // The catalog spells the codes out (Analytics names no Wardrobe type); this keeps the lists equal.
        foreach (var code in BulkAddCopy.Refusals.Keys)
        {
            Assert.Null(Reject("bulk_add_result", $$"""{"status":"Refused","refusal":"{{code}}","durationMs":1,"pieces":0,"preTicked":0,"unseparated":0,"duplicateHints":0}"""));
        }

        foreach (var code in Enum.GetNames<BulkFailureCode>())
        {
            Assert.Null(Reject("bulk_add_result", $$"""{"status":"Failed","failure":"{{code}}","durationMs":1,"pieces":0,"preTicked":0,"unseparated":0,"duplicateHints":0}"""));
        }

        Assert.Equal(7, BulkAddCopy.Refusals.Count);
        Assert.Equal(5, Enum.GetNames<BulkFailureCode>().Length);
        Assert.NotNull(Reject("bulk_add_result", """{"status":"Refused","refusal":"made_up","durationMs":1,"pieces":0,"preTicked":0,"unseparated":0,"duplicateHints":0}"""));
    }

    [Fact]
    public void TheBulkAddEventsRejectIdentifiersCountsOutOfRangeAndStrangers()
    {
        Assert.NotNull(Reject("bulk_add_start", """{"source":"catalog"}"""));
        Assert.NotNull(Reject("bulk_add_start", """{"source":"camera","batchId":"3f2b8d30-8f0e-4c1a-9a53-0f4d1c1b7f10"}"""));
        Assert.NotNull(Reject("bulk_add_cancel", """{"stage":"Created"}"""));
        Assert.NotNull(Reject("bulk_add_result", """{"status":"Ready","durationMs":1,"pieces":13,"preTicked":0,"unseparated":0,"duplicateHints":0}"""));
        Assert.NotNull(Reject("bulk_add_result", """{"status":"Ready","durationMs":1,"pieces":3}"""));
        Assert.NotNull(Reject("bulk_add_confirm", """{"shown":3,"ticked":3,"preTickedTicked":2,"preTickedLeft":0,"extraTicked":1,"categoryEdits":1,"colourEdits":0,"durationMs":1,"pieceId":"x"}"""));
    }

    [Fact]
    public void AValidBatchPasses()
    {
        var request = Batch(
            Event("app_open"),
            Event("screen_view", """{"screen":"product_detail"}"""),
            Event("product_view", $$"""{"productId":"{{ProductId}}","source":"feed"}"""),
            Event("search", """{"queryLength":7}"""));

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void TheRequestValidatorLeavesNamesAndPropertiesToTheCatalog()
    {
        var request = Batch(Event("purchase"), Event("search", """{"query":"gömlek","email":"a@b.com"}"""));

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void EveryEventOfTheAllowListCanBeSentWithItsDocumentedProperties()
    {
        var persona = Guid.NewGuid();
        var samples = new Dictionary<string, string?>
        {
            ["app_open"] = null,
            ["app_background"] = null,
            ["screen_view"] = """{"screen":"home"}""",
            ["product_view"] = $$"""{"productId":"{{ProductId}}","personaId":"{{persona}}","source":"feed"}""",
            ["product_list_view"] = """{"sort":"priceAsc","filters":"category,color"}""",
            ["swipe"] = $$"""{"personaId":"{{persona}}","decision":"Like"}""",
            ["onboarding_step"] = """{"step":"style_cards","action":"next"}""",
            ["alert_created"] = """{"kind":"PriceDrop"}""",
            ["buy_click"] = $$"""{"offerId":"{{ProductId}}","source":"product_detail"}""",
            ["search"] = """{"queryLength":0}""",
            ["notification_open"] = """{"notificationId":42,"kind":"BackInStock"}""",
            ["wardrobe_add_start"] = """{"source":"camera"}""",
            ["wardrobe_add_done"] = """{"status":"Ready","durationMs":7100}""",
            ["outfit_view"] = """{"kind":"Daily","status":"NeedsOne","occasion":"office"}""",
            ["outfit_shuffle"] = null,
            ["outfit_feedback"] = """{"action":"Dismiss","reason":"TooFormal"}""",
            ["outfit_gap_click"] = null,
            ["budget_outfit_view"] = """{"budgetBand":"500to1500","outfitCount":3,"result":"fits","occasion":"office"}""",
            ["budget_outfit_shuffle"] = """{"seed":1}""",
            ["trip_plan_create"] = """{"days":4,"pieceCount":7,"coveragePercent":100,"gapCount":0}""",
            ["trip_plan_view"] = """{"days":4,"source":"saved"}""",
            ["trip_gap_click"] = """{"slot":"Shoes"}""",
            ["wardrobe_pairings_view"] = """{"companionTotal":12,"hasUnlock":true}""",
            ["wardrobe_unlock_click"] = null,
            ["wardrobe_summary_view"] = """{"pieceCount":24,"unwornCount":5,"hasEstimate":true}""",
            ["size_prompt_view"] = $$"""{"promptId":"{{ProductId}}"}""",
            ["size_prompt_answer"] = $$"""{"promptId":"{{ProductId}}","answer":"BoughtTooSmall"}""",
            ["post_view"] = $$"""{"postId":"{{ProductId}}","source":"feed","position":2}""",
            ["post_tag_tap"] = $$"""{"postId":"{{ProductId}}","tagKind":"Product","hasOffer":true}""",
            ["post_like"] = $$"""{"postId":"{{ProductId}}","liked":true}""",
            ["post_create_start"] = """{"kind":"Photo","source":"library"}""",
            ["post_create_done"] = """{"kind":"Photo","status":"NeedsReview","tagCount":2,"visibility":"Followers","durationMs":38000}""",
            ["post_report"] = $$"""{"postId":"{{ProductId}}","reason":"Harassment"}""",
            ["creator_profile_view"] = $$"""{"creatorId":"{{persona}}"}""",
            ["try_with_wardrobe"] = $$"""{"postId":"{{ProductId}}","anchorCount":1,"outfitCount":2}""",
            ["auth_external"] = """{"provider":"google","isNewUser":true}""",
            ["lens_open"] = """{"entry":"explore"}""",
            ["lens_search"] = """{"regionCount":2,"resultCount":17,"durationMs":2140,"source":"camera"}""",
            ["lens_result_tap"] = $$"""{"productId":"{{ProductId}}","position":0,"similarity":"close"}""",
            ["lens_add_to_wardrobe"] = """{"family":"Outerwear"}""",
            ["league_join"] = null,
            ["league_leave"] = null,
            ["league_vote"] = """{"choice":"left","counted":true}""",
            ["league_report"] = null,
            ["league_results_view"] = """{"status":"UnderReview"}""",
            ["league_table_view"] = null,
            ["ask_create"] = $$"""{"askId":"{{ProductId}}","kind":"Pair"}""",
            ["ask_share"] = $$"""{"askId":"{{ProductId}}"}""",
            ["ask_view"] = $$"""{"askId":"{{ProductId}}","status":"Hidden"}""",
            ["ask_close"] = $$"""{"askId":"{{ProductId}}"}""",
            ["ask_answer_hide"] = $$"""{"askId":"{{ProductId}}"}""",
            ["bulk_add_start"] = """{"source":"library"}""",
            ["bulk_add_result"] = """{"status":"Failed","failure":"VisionBusy","durationMs":26000,"pieces":0,"preTicked":0,"unseparated":0,"duplicateHints":0}""",
            ["bulk_add_confirm"] = """{"shown":5,"ticked":4,"preTickedTicked":3,"preTickedLeft":1,"extraTicked":1,"categoryEdits":2,"colourEdits":0,"durationMs":52000}""",
            ["bulk_add_cancel"] = """{"stage":"Processing"}""",
        };

        Assert.Equal(samples.Keys.Order(), AnalyticsEventCatalog.Names.Order());

        foreach (var (name, properties) in samples)
        {
            Assert.Null(Reject(name, properties));
        }
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("App_Open")]
    [InlineData("app_open ")]
    [InlineData("")]
    [InlineData(null)]
    public void AnEventOutsideTheAllowListIsRejectedAsUnknown(string? name) =>
        Assert.Equal("unknown_event", Reject(name));

    [Theory]
    [InlineData("email")]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("token")]
    [InlineData("Email")]
    [InlineData("E-Mail")]
    [InlineData("e_mail")]
    [InlineData("PHONE")]
    public void PropertyKeysThatNamePersonalDataMakeTheEventsPropertiesInvalid(string key)
    {
        var json = $$"""{"screen":"home","{{key}}":"someone"}""";

        Assert.Equal("invalid_properties", Reject("screen_view", json));

        var problem = Assert.Single(AnalyticsEventCatalog.Check("screen_view", Props(json)));
        Assert.Equal(key, problem.Key);
        Assert.Contains("personal data", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchCarriesItsLengthAndNeverItsText()
    {
        Assert.Equal("invalid_properties", Reject("search", """{"queryLength":5,"query":"gömlek"}"""));
        Assert.Equal(["query"], BadKeys("search", """{"queryLength":5,"query":"gömlek"}"""));
    }

    [Fact]
    public void PropertiesNotListedForTheEventAreInvalid()
    {
        Assert.Equal("invalid_properties", Reject("app_open", """{"anything":1}"""));
        Assert.Equal(["anything"], BadKeys("app_open", """{"anything":1}"""));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"screen":null}""")]
    [InlineData("""{"screen":""}""")]
    [InlineData("""{"screen":"free text with spaces"}""")]
    [InlineData("""{"screen":"a@b.com"}""")]
    [InlineData("""{"screen":5}""")]
    [InlineData("""{"screen":{"a":1}}""")]
    [InlineData("""{"screen":["a"]}""")]
    public void RequiredPropertiesMustBeFlatIdentifiers(string json)
    {
        Assert.Equal("invalid_properties", Reject("screen_view", json));
        Assert.Contains("screen", BadKeys("screen_view", json));
    }

    [Fact]
    public void AMissingPropertiesObjectIsInvalidWhenTheEventNeedsProperties()
    {
        Assert.Equal("invalid_properties", Reject("screen_view"));
        Assert.Null(Reject("app_open"));
    }

    [Theory]
    [InlineData("product_view", """{"productId":"not-a-guid","source":"feed"}""", "productId")]
    [InlineData("product_view", """{"productId":"00000000-0000-0000-0000-000000000000","source":"feed"}""", "productId")]
    [InlineData("product_view", """{"productId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11"}""", "source")]
    [InlineData("swipe", """{"personaId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","decision":"Love"}""", "decision")]
    [InlineData("onboarding_step", """{"step":"intro","action":"back"}""", "action")]
    [InlineData("alert_created", """{"kind":"Weekly"}""", "kind")]
    [InlineData("search", """{"queryLength":-1}""", "queryLength")]
    [InlineData("search", """{"queryLength":201}""", "queryLength")]
    [InlineData("search", """{"queryLength":"7"}""", "queryLength")]
    [InlineData("search", """{"queryLength":1.5}""", "queryLength")]
    [InlineData("notification_open", """{"notificationId":0,"kind":"System"}""", "notificationId")]
    [InlineData("wardrobe_add_start", """{"source":"clipboard"}""", "source")]
    [InlineData("wardrobe_add_done", """{"status":"Done","durationMs":10}""", "status")]
    [InlineData("wardrobe_add_done", """{"status":"Ready","durationMs":-1}""", "durationMs")]
    [InlineData("outfit_view", """{"kind":"Weekly","status":"Complete"}""", "kind")]
    [InlineData("outfit_view", """{"kind":"Daily","status":"Complete","occasion":"picnic"}""", "occasion")]
    [InlineData("outfit_feedback", """{"action":"Love"}""", "action")]
    [InlineData("outfit_feedback", """{"action":"Dismiss","reason":"Boring"}""", "reason")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":-1,"hasUnlock":false}""", "companionTotal")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":501,"hasUnlock":false}""", "companionTotal")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":"3","hasUnlock":false}""", "companionTotal")]
    [InlineData("wardrobe_pairings_view", """{"hasUnlock":true}""", "companionTotal")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":3}""", "hasUnlock")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":3,"hasUnlock":"true"}""", "hasUnlock")]
    [InlineData("wardrobe_pairings_view", """{"companionTotal":3,"hasUnlock":1}""", "hasUnlock")]
    [InlineData("wardrobe_summary_view", """{"pieceCount":-1,"unwornCount":0,"hasEstimate":false}""", "pieceCount")]
    [InlineData("wardrobe_summary_view", """{"pieceCount":3,"unwornCount":0}""", "hasEstimate")]
    [InlineData("wardrobe_summary_view", """{"pieceCount":3,"unwornCount":0,"hasEstimate":false,"total":1200}""", "total")]
    [InlineData("size_prompt_view", """{"promptId":"not-a-guid"}""", "promptId")]
    [InlineData("size_prompt_view", """{}""", "promptId")]
    [InlineData("size_prompt_answer", """{"promptId":"00000000-0000-0000-0000-000000000001","answer":"Sort of"}""", "answer")]
    [InlineData("size_prompt_answer", """{"promptId":"00000000-0000-0000-0000-000000000001","answer":"BoughtFits","size":"M"}""", "size")]
    [InlineData("auth_external", """{"provider":"facebook","isNewUser":false}""", "provider")]
    [InlineData("auth_external", """{"isNewUser":true}""", "provider")]
    [InlineData("auth_external", """{"provider":"apple"}""", "isNewUser")]
    [InlineData("auth_external", """{"provider":"apple","isNewUser":"true"}""", "isNewUser")]
    [InlineData("auth_external", """{"provider":"apple","isNewUser":true,"email":"a@b.com"}""", "email")]
    [InlineData("lens_search", """{"regionCount":7,"resultCount":0,"durationMs":10,"source":"camera"}""", "regionCount")]
    [InlineData("lens_search", """{"regionCount":1,"resultCount":0,"durationMs":10,"source":"upload"}""", "source")]
    [InlineData("lens_result_tap", """{"productId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","position":0,"similarity":0.93}""", "similarity")]
    [InlineData("lens_add_to_wardrobe", """{"family":"Hat"}""", "family")]
    [InlineData("lens_open", """{"entry":"explore","imageUrl":"x"}""", "imageUrl")]
    [InlineData("league_vote", """{"choice":"both","counted":true}""", "choice")]
    [InlineData("league_vote", """{"choice":"left"}""", "counted")]
    [InlineData("league_vote", """{"choice":"right","counted":"yes"}""", "counted")]
    [InlineData("league_vote", """{"choice":"skip","counted":false,"pairId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11"}""", "pairId")]
    [InlineData("league_vote", """{"choice":"left","counted":true,"entryId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11"}""", "entryId")]
    [InlineData("league_report", """{"entryId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11"}""", "entryId")]
    [InlineData("league_join", """{"alias":"Mavi Ceket"}""", "alias")]
    [InlineData("league_results_view", """{"status":"Pending"}""", "status")]
    [InlineData("league_results_view", """{"status":"Final","imageUrl":"x"}""", "imageUrl")]
    [InlineData("league_table_view", """{"rank":3}""", "rank")]
    [InlineData("ask_create", """{"askId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","kind":"Triple"}""", "kind")]
    [InlineData("ask_create", """{"askId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","kind":"Single","token":"x"}""", "token")]
    [InlineData("ask_view", """{"askId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","status":"Deleted"}""", "status")]
    [InlineData("ask_share", """{"askId":"not-an-id"}""", "askId")]
    [InlineData("ask_close", """{"askId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11","name":"Ayse"}""", "name")]
    [InlineData("wardrobe_unlock_click", """{"productId":"3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11"}""", "productId")]
    [InlineData("product_list_view", """{"sort":"cheapest","filters":""}""", "sort")]
    [InlineData("product_list_view", """{"sort":"newest","filters":"a b"}""", "filters")]
    public void PropertyValuesAreCheckedAgainstTheirType(string name, string json, string expectedKey)
    {
        Assert.Equal("invalid_properties", Reject(name, json));
        Assert.Contains(expectedKey, BadKeys(name, json));
    }

    [Fact]
    public void OptionalPropertiesMayBeLeftOutOrNull()
    {
        Assert.Null(Reject("product_view", $$"""{"productId":"{{ProductId}}","source":"feed"}"""));
        Assert.Null(Reject("product_view", $$"""{"productId":"{{ProductId}}","personaId":null,"source":"feed"}"""));
    }

    [Fact]
    public void PropertiesAreLimitedToTwoKilobytes()
    {
        var oversized = new Dictionary<string, JsonElement>
        {
            ["screen"] = JsonSerializer.SerializeToElement(new string('a', AnalyticsEventCatalog.MaxPropertiesBytes)),
        };

        Assert.Equal("invalid_properties", AnalyticsEventCatalog.Reject("screen_view", oversized));
    }

    [Fact]
    public void ABatchHoldsOneToFiftyEvents()
    {
        Assert.False(_validator.Validate(new RecordEventsRequest([])).IsValid);
        Assert.False(_validator.Validate(new RecordEventsRequest(null!)).IsValid);
        Assert.True(_validator.Validate(Batch([.. Enumerable.Range(0, 50).Select(_ => Event())])).IsValid);
        Assert.False(_validator.Validate(Batch([.. Enumerable.Range(0, 51).Select(_ => Event())])).IsValid);
    }

    [Fact]
    public void ANullEventInTheBatchIsAMalformedBody() =>
        Assert.False(_validator.Validate(Batch(Event(), null!)).IsValid);

    [Theory]
    [InlineData("Windows")]
    [InlineData("")]
    [InlineData(null)]
    public void ThePlatformIsIosOrAndroid(string? platform)
    {
        var result = _validator.Validate(Batch(Event() with { Platform = platform! }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Events[0].Platform");
        Assert.True(_validator.Validate(Batch(Event() with { Platform = "android" })).IsValid);
    }

    [Fact]
    public void SessionVersionAndTimeAreRequiredAndBounded()
    {
        Assert.False(_validator.Validate(Batch(Event() with { SessionId = "" })).IsValid);
        Assert.False(_validator.Validate(Batch(Event() with { SessionId = new string('s', 65) })).IsValid);
        Assert.False(_validator.Validate(Batch(Event() with { SessionId = "has space" })).IsValid);
        Assert.False(_validator.Validate(Batch(Event() with { AppVersion = "" })).IsValid);
        Assert.False(_validator.Validate(Batch(Event() with { AppVersion = new string('1', 41) })).IsValid);
        Assert.False(_validator.Validate(Batch(Event() with { OccurredAt = null })).IsValid);
        Assert.True(_validator.Validate(Batch(Event() with { SessionId = new string('s', 64), AppVersion = new string('1', 40) })).IsValid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0b1f6c3e-5f7a-4b19-9d57-0d3a8f3a9c11", true)]
    [InlineData("a-stable_random.id:1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("a@b.com", false)]
    public void TheAnonymousIdIsOptionalButWhenPresentIdentifierShaped(string? anonymousId, bool valid) =>
        Assert.Equal(valid, _validator.Validate(Batch(Event(anonymousId: anonymousId))).IsValid);

    [Fact]
    public void TheAnonymousIdIsAtMost64Characters()
    {
        Assert.True(_validator.Validate(Batch(Event(anonymousId: new string('a', 64)))).IsValid);
        Assert.False(_validator.Validate(Batch(Event(anonymousId: new string('a', 65)))).IsValid);
    }
}
