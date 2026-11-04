using System.Text.Json;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Notification;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.EntityFrameworkCore;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The weekly matches job against a real database. Its candidates are the newest products of the shared database, which any test running
/// alongside may add to, so every test runs the job over the products it created itself.
/// </summary>
public sealed class WeeklyMatchesTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A persona that, with full confidence, likes only <paramref name="color"/> (and scores ≥ 70 on a regular product of it).</summary>
    private async Task<Guid> CreatePersonaAsync(Guid userId, string name, string color, int sortOrder = 0, double colorWeight = 1)
    {
        return await factory.WithDbAsync(async db =>
        {
            var persona = new Persona { UserId = userId, Name = name, Icon = "sun", SortOrder = sortOrder };
            db.Personas.Add(persona);
            db.PersonaStyleProfiles.Add(new PersonaStyleProfile
            {
                PersonaId = persona.Id,
                ColorWeights = new Dictionary<string, double> { [color] = colorWeight },
                FamilyWeights = Enum.GetNames<StyleFamily>().ToDictionary(family => family, _ => 1d),
                FitWeights = Enum.GetNames<ProductFit>().ToDictionary(fit => fit, _ => 1d),
                SeasonWeights = new Dictionary<string, double> { ["Spring"] = 1, ["Summer"] = 1, ["Autumn"] = 1, ["Winter"] = 1 },
                FormalityMean = 3,
                LikeCount = 12,
                Confidence = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);

            return persona.Id;
        });
    }

    private static async Task<Guid> SeedColoredProductAsync(HttpClient client, string color)
    {
        var brand = CatalogClientExtensions.Unique("Wk");
        var sellerSlug = $"{CatalogClientExtensions.Slug(brand)}-store";
        var externalId = $"wk-{Guid.NewGuid():N}";
        await client.UpsertOkAsync(sellerSlug, [CatalogClientExtensions.Item(externalId, 800m, brand, "Gömlek", category: "ust-giyim/gomlek", color: color)], Ct);

        return await client.FindProductIdAsync(CatalogClientExtensions.Slug(brand), Ct);
    }

    private async Task<TestUser> CreateUserWithDeviceAsync()
    {
        var user = await factory.CreateUserAsync(Ct);
        await user.Client.RegisterDeviceAsync(cancellationToken: Ct);

        return user;
    }

    private static Guid[] ProductIds(NotificationEntity notification)
    {
        using var data = JsonDocument.Parse(notification.Data);

        return [.. data.RootElement.GetProperty("productIds").EnumerateArray().Select(id => id.GetGuid())];
    }

    [Fact]
    public async Task AUserWithAMatchingNewProductGetsOneDigestPerWeek()
    {
        using var user = await CreateUserWithDeviceAsync();
        var personaId = await CreatePersonaAsync(user.Id, "Hafta sonu", "Orange");
        var productId = await SeedColoredProductAsync(user.Client, "Orange");

        var created = await factory.SendWeeklyMatchesAsync(user.Id, [productId], Ct);

        Assert.Equal(1, created);
        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal(NotificationKind.NewMatches, notification.Kind);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal("\"Hafta sonu\" tarzına %90 üzeri uyumlu 1 yeni parça", notification.Title);
        Assert.Equal([productId], ProductIds(notification));
        Assert.StartsWith($"newmatches:{personaId}:", notification.DedupeKey, StringComparison.Ordinal);

        using (var data = JsonDocument.Parse(notification.Data))
        {
            Assert.Equal(personaId, data.RootElement.GetProperty("personaId").GetGuid());
            Assert.Equal($"kombin://matches/{personaId}", data.RootElement.GetProperty("deepLink").GetString());
        }

        // Same ISO week, same persona: a second run adds nothing.
        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(user.Id, [productId], Ct));
        Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));

        // And it goes out through the normal sender.
        await factory.SendPushesAsync(user.Id, Ct);
        Assert.Equal(NotificationStatus.Sent, Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct)).Status);
    }

    [Fact]
    public async Task OnlyTheThreeBestProductsAreNamed()
    {
        using var user = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(user.Id, "Ofis", "Purple");
        var products = new List<Guid>();

        for (var index = 0; index < 4; index++)
        {
            products.Add(await SeedColoredProductAsync(user.Client, "Purple"));
        }

        await factory.SendWeeklyMatchesAsync(user.Id, products, Ct);

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Contains("uyumlu 3 yeni parça", notification.Title, StringComparison.Ordinal);

        // Equal scores: the newest three.
        Assert.Equal(products.AsEnumerable().Reverse().Take(3).Order(), ProductIds(notification).Order());
    }

    [Fact]
    public async Task ProductsBelowTheThresholdAreNotWorthANotification()
    {
        using var user = await CreateUserWithDeviceAsync();

        // The color is only weakly liked, so the best possible score stays under 70.
        await CreatePersonaAsync(user.Id, "Ofis", "Pink", colorWeight: 0.1);
        var pink = await SeedColoredProductAsync(user.Client, "Pink");

        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(user.Id, [pink], Ct));
        Assert.Empty(await factory.NotificationsOfAsync(user.Id, Ct));
    }

    [Fact]
    public async Task TheActivePersonaIsTheOneThatCounts()
    {
        using var user = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(user.Id, "Ofis", "Grey", sortOrder: 0);
        var active = await CreatePersonaAsync(user.Id, "Gece", "White", sortOrder: 1);
        await factory.WithDbAsync(db =>
        {
            db.UserProfiles.Add(new UserProfile { UserId = user.Id, ActivePersonaId = active });

            return db.SaveChangesAsync(Ct);
        });
        var white = await SeedColoredProductAsync(user.Client, "White");
        var grey = await SeedColoredProductAsync(user.Client, "Grey");

        await factory.SendWeeklyMatchesAsync(user.Id, [white, grey], Ct);

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.Equal([white], ProductIds(notification));
        Assert.StartsWith("\"Gece\"", notification.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnActivePersonaTheFirstOneIsUsed()
    {
        using var user = await CreateUserWithDeviceAsync();
        var first = await CreatePersonaAsync(user.Id, "Ofis", "Yellow", sortOrder: 0);
        await CreatePersonaAsync(user.Id, "Gece", "Brown", sortOrder: 1);
        var yellow = await SeedColoredProductAsync(user.Client, "Yellow");
        var brown = await SeedColoredProductAsync(user.Client, "Brown");

        await factory.SendWeeklyMatchesAsync(user.Id, [yellow, brown], Ct);

        var notification = Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
        Assert.StartsWith($"newmatches:{first}:", notification.DedupeKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendationsThatAreOffOrCappedAtZeroSendNothing()
    {
        using var off = await CreateUserWithDeviceAsync();
        using var capped = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(off.Id, "Ofis", "Beige");
        await CreatePersonaAsync(capped.Id, "Ofis", "Beige");
        var beige = await SeedColoredProductAsync(off.Client, "Beige");
        (await off.Client.PutPreferencesAsync(new { recommendations = false }, Ct)).Dispose();
        (await capped.Client.PutPreferencesAsync(new { maxRecommendationsPerWeek = 0 }, Ct)).Dispose();

        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(off.Id, [beige], Ct));
        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(capped.Id, [beige], Ct));
        Assert.Empty(await factory.NotificationsOfAsync(off.Id, Ct));
        Assert.Empty(await factory.NotificationsOfAsync(capped.Id, Ct));
    }

    [Fact]
    public async Task AUserWhoReachedTheirWeeklyLimitGetsNoMore()
    {
        using var user = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(user.Id, "Ofis", "Other");
        await factory.WithDbAsync(async db =>
        {
            db.Notifications.Add(new NotificationEntity
            {
                UserId = user.Id,
                Kind = NotificationKind.NewMatches,
                Title = "Eski",
                Body = "Eski",
                DedupeKey = $"newmatches:{Guid.NewGuid()}:2020-W01",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                ScheduledFor = DateTimeOffset.UtcNow.AddDays(-2),
                SentAt = DateTimeOffset.UtcNow.AddDays(-2),
                Status = NotificationStatus.Sent,
            });
            await db.SaveChangesAsync(Ct);

            return 0;
        });
        (await user.Client.PutPreferencesAsync(new { maxRecommendationsPerWeek = 1 }, Ct)).Dispose();
        var other = await SeedColoredProductAsync(user.Client, "Other");

        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(user.Id, [other], Ct));
        Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct));
    }

    [Fact]
    public async Task AUserWithoutADeviceIsNotNotified()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await CreatePersonaAsync(user.Id, "Ofis", "Navy");
        var navy = await SeedColoredProductAsync(user.Client, "Navy");

        Assert.Equal(0, await factory.SendWeeklyMatchesAsync(user.Id, [navy], Ct));
        Assert.Empty(await factory.NotificationsOfAsync(user.Id, Ct));
    }

    [Fact]
    public async Task AProductOutsideTheFilterIsNoCandidate()
    {
        using var user = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(user.Id, "Ofis", "Red");
        var mine = await SeedColoredProductAsync(user.Client, "Red");
        await SeedColoredProductAsync(user.Client, "Red");

        await factory.SendWeeklyMatchesAsync(user.Id, [mine], Ct);

        Assert.Equal([mine], ProductIds(Assert.Single(await factory.NotificationsOfAsync(user.Id, Ct))));
    }

    [Fact]
    public async Task WithoutAFilterEveryNewProductIsACandidate()
    {
        using var user = await CreateUserWithDeviceAsync();
        await CreatePersonaAsync(user.Id, "Ofis", "Green");
        await SeedColoredProductAsync(user.Client, "Green");

        // Which products it names depends on the rest of the shared database; that it finds one does not.
        Assert.Equal(1, await factory.SendWeeklyMatchesAsync(user.Id, null, Ct));
    }
}
