using System.Text.Json;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Analytics.RecordEvents;

namespace Kombin.Api.Tests;

public class AnalyticsValidatorTests
{
    private const string ProductId = "3f2c8f0e-8f3e-4c1b-9a53-5d7b0b3a1c11";

    private readonly RecordEventsValidator _validator = new();

    private static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static EventItem Event(string name = "app_open", string? properties = null, string? anonymousId = "anon-1") =>
        new(name, properties is null ? null : Props(properties), "session-1", "Ios", "1.0.0", DateTimeOffset.UtcNow, anonymousId);

    private static RecordEventsRequest Batch(params EventItem[] events) => new([.. events]);

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
        };

        Assert.Equal(samples.Keys.Order(), AnalyticsEventCatalog.Names.Order());

        foreach (var (name, properties) in samples)
        {
            var result = _validator.Validate(Batch(Event(name, properties)));
            Assert.True(result.IsValid, $"{name}: {string.Join("; ", result.Errors.Select(error => error.ErrorMessage))}");
        }
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("App_Open")]
    [InlineData("app_open ")]
    [InlineData("")]
    [InlineData(null)]
    public void AnEventOutsideTheAllowListIsRejectedWithItsOwnErrorCode(string? name)
    {
        var result = _validator.Validate(Batch(Event("app_open"), Event(name!)));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("Analytics.UnknownEvent", failure.ErrorCode);
        Assert.Equal("Events[1].Name", failure.PropertyName);
    }

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
    public void PropertyKeysThatNamePersonalDataAreRejectedOnAnyEvent(string key)
    {
        var json = $$"""{"screen":"home","{{key}}":"someone"}""";

        var result = _validator.Validate(Batch(Event("screen_view", json)));

        var failure = Assert.Single(result.Errors);
        Assert.Equal($"Events[0].Properties.{key}", failure.PropertyName);
        Assert.Contains("personal data", failure.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchCarriesItsLengthAndNeverItsText()
    {
        var result = _validator.Validate(Batch(Event("search", """{"queryLength":5,"query":"gömlek"}""")));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("Events[0].Properties.query", failure.PropertyName);
    }

    [Fact]
    public void PropertiesNotListedForTheEventAreRejected()
    {
        var result = _validator.Validate(Batch(Event("app_open", """{"anything":1}""")));

        Assert.Equal("Events[0].Properties.anything", Assert.Single(result.Errors).PropertyName);
    }

    [Theory]
    [InlineData("""{}""", "screen")]
    [InlineData("""{"screen":null}""", "screen")]
    [InlineData("""{"screen":""}""", "screen")]
    [InlineData("""{"screen":"free text with spaces"}""", "screen")]
    [InlineData("""{"screen":"a@b.com"}""", "screen")]
    [InlineData("""{"screen":5}""", "screen")]
    [InlineData("""{"screen":{"a":1}}""", "screen")]
    [InlineData("""{"screen":["a"]}""", "screen")]
    public void RequiredPropertiesMustBeFlatIdentifiers(string json, string expectedKey)
    {
        var result = _validator.Validate(Batch(Event("screen_view", json)));

        Assert.Contains(result.Errors, failure => failure.PropertyName == $"Events[0].Properties.{expectedKey}");
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
    [InlineData("product_list_view", """{"sort":"cheapest","filters":""}""", "sort")]
    [InlineData("product_list_view", """{"sort":"newest","filters":"a b"}""", "filters")]
    public void PropertyValuesAreCheckedAgainstTheirType(string name, string json, string expectedKey)
    {
        var result = _validator.Validate(Batch(Event(name, json)));

        Assert.Contains(result.Errors, failure => failure.PropertyName == $"Events[0].Properties.{expectedKey}");
    }

    [Fact]
    public void OptionalPropertiesMayBeLeftOutOrNull()
    {
        Assert.True(_validator.Validate(Batch(Event("product_view", $$"""{"productId":"{{ProductId}}","source":"feed"}"""))).IsValid);
        Assert.True(_validator.Validate(Batch(Event("product_view", $$"""{"productId":"{{ProductId}}","personaId":null,"source":"feed"}"""))).IsValid);
    }

    [Fact]
    public void PropertiesAreLimitedToTwoKilobytes()
    {
        var oversized = new Dictionary<string, JsonElement>
        {
            ["screen"] = JsonSerializer.SerializeToElement(new string('a', AnalyticsEventCatalog.MaxPropertiesBytes)),
        };

        var result = _validator.Validate(Batch(Event("screen_view") with { Properties = oversized }));

        Assert.Contains(result.Errors, failure => failure.PropertyName == "Events[0].Properties" && failure.ErrorMessage.Contains("larger", StringComparison.Ordinal));
    }

    [Fact]
    public void ABatchHoldsOneToFiftyEvents()
    {
        Assert.False(_validator.Validate(new RecordEventsRequest([])).IsValid);
        Assert.False(_validator.Validate(new RecordEventsRequest(null!)).IsValid);
        Assert.True(_validator.Validate(Batch([.. Enumerable.Range(0, 50).Select(_ => Event())])).IsValid);
        Assert.False(_validator.Validate(Batch([.. Enumerable.Range(0, 51).Select(_ => Event())])).IsValid);
    }

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
