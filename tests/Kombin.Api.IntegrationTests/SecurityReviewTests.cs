using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Engagement;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The findings of the security review (<c>docs/security-review.md</c>) that were fixed, each proven by the attack it stops. Every test here
/// failed before its fix.
/// </summary>
public sealed class SecurityReviewTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // H1. Account pre-hijacking: an attacker registers the victim's address with a password (registration never proves the address), the
    // victim later signs in with Google and lands in that account; the attacker kept the password and a session and could read everything.
    [Fact]
    public async Task AGoogleSignInThatClaimsAPasswordAccountByEmailEndsThePasswordAndItsSessions()
    {
        using var attacker = factory.CreateAnonymousClient();
        var victimEmail = ApiClientExtensions.NewEmail();
        var registered = await attacker.RegisterAndReadAsync(victimEmail, Ct);
        using var rotation = await attacker.RefreshAsync(registered.RefreshToken, Ct);
        var planted = await rotation.ReadTokensAsync(Ct);

        using var victim = factory.CreateAnonymousClient();
        using var google = await victim.ExternalSignInAsync("google", FakeExternalTokenVerifier.Token(FakeExternalTokenVerifier.NewSubject(), victimEmail), cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        var victimTokens = await google.ReadTokensAsync(Ct);
        using (var me = await victim.GetMeAsync(victimTokens.AccessToken, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        // The planted password no longer opens the account, and neither does the session it made.
        using var login = await attacker.LoginAsync(victimEmail, ApiClientExtensions.DefaultPassword, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("Identity.InvalidCredentials", (await login.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        using var refresh = await attacker.RefreshAsync(planted.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Replaying the rotated one would normally end every session of the user (reuse detection); the tokens are gone, so it cannot be
        // used to sign the victim out either.
        using var replay = await attacker.RefreshAsync(registered.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The victim's own refresh token was issued after the cut and keeps working.
        using var victimRefresh = await victim.RefreshAsync(victimTokens.RefreshToken, Ct);
        Assert.Equal(HttpStatusCode.OK, victimRefresh.StatusCode);
    }

    // H2. SSRF: catalog image URLs come from scraped pages; the embedding job and from-product downloaded whatever they named, from inside
    // the compose network (MinIO, the vision service, PostgreSQL's host, the cloud metadata address), following any redirect.
    [Theory]
    [InlineData("literal-metadata")]
    [InlineData("literal-private")]
    [InlineData("literal-loopback")]
    [InlineData("dns-private")]
    [InlineData("dns-metadata")]
    [InlineData("redirect-to-private")]
    public async Task TheEmbeddingJobNeverFetchesAnInternalAddress(string kind)
    {
        using var client = factory.CreateAnonymousClient();
        var unique = Guid.NewGuid().ToString("N");
        var target = kind switch
        {
            "literal-metadata" => $"http://169.254.169.254/latest/meta-data/{unique}.jpg",
            "literal-private" => $"http://172.30.0.5:9000/kombin/{unique}.jpg",
            "literal-loopback" => $"http://127.0.0.1:8080/{unique}.jpg",
            "dns-private" => $"https://{FakeOutboundResolver.PrivateHost}/{unique}.jpg",
            "dns-metadata" => $"http://{FakeOutboundResolver.MetadataHost}/{unique}.jpg",
            _ => $"http://10.1.2.3/{unique}.jpg",
        };
        var imageUrl = kind == "redirect-to-private" ? $"https://img.example.com/{unique}/redirect?to={Uri.EscapeDataString(target)}" : target;
        var productId = await client.SeedCatalogProductAsync("ust-giyim/tisort", "Navy", 2, imageUrl, Ct);

        var run = await factory.RunJobAsync<ProductEmbeddingJob, EmbeddingRunSummary>(job => job.EmbedAsync([productId], int.MaxValue, null, Ct));

        Assert.Equal(1, run.Unusable);
        var row = await factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().SingleAsync(candidate => candidate.ProductId == productId, Ct));
        Assert.Equal((ProductEmbeddingStatus.Unusable, "image_refused_address"), (row.Status, row.FailureReason));
        Assert.Equal(0, FakeCatalogImageHandler.Requests(target));
    }

    [Fact]
    public async Task AddingAProductToTheWardrobeNeverCopiesAnInternalAddressIntoAPublicImage()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var imageUrl = $"http://{FakeOutboundResolver.PrivateHost}/{Guid.NewGuid():N}.jpg";
        var productId = await user.Client.SeedCatalogProductAsync("ayakkabi/sneaker", "White", 2, imageUrl, Ct);

        using var created = await user.Client.FromProductAsync(productId, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var row = await factory.ItemRowAsync(itemId, Ct);
        Assert.Equal(((string?)null, (string?)null), (row.ImageThumbKey, row.ImageCutoutKey));
        Assert.Equal(0, FakeCatalogImageHandler.Requests(imageUrl));
    }

    // M. Creator earnings inflation: every click on a creator's tag earned a referral, so one viewer (or a script, or the creator signed out)
    // reloading the same link counted again and again. Now one viewer earns the creator once per post and product a day.
    [Fact]
    public async Task OneViewerEarnsACreatorOnceAPostAndProductADay()
    {
        using var creator = await factory.CreateUserAsync(Ct);
        using var viewer = await factory.CreateUserAsync(Ct);
        using var anonymous = factory.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.Header, "198.51.100.23");
        var productId = await anonymous.SeedProductAsync("ust-giyim/gomlek", "White", 3, price: 650m, cancellationToken: Ct);
        await anonymous.MakeCreatorAsync(creator.Id, $"earn_{Guid.NewGuid():N}"[..20], Ct);
        var postId = await factory.CreateLivePostAsync(creator.Client, tags: [SocialClientExtensions.ProductTag(productId)], cancellationToken: Ct);
        var goPath = (await viewer.Client.GetJsonAsync($"/api/v1/posts/{postId}", Ct)).GetProperty("tags")[0].GetProperty("offer").GetProperty("goPath").GetString()!;

        var first = await ClickAsync(viewer.Client, goPath);
        var repeat = await ClickAsync(viewer.Client, goPath);
        var anonymousFirst = await ClickAsync(anonymous, goPath);
        var anonymousRepeat = await ClickAsync(anonymous, goPath);

        Assert.Equal((creator.Id, (Guid?)null), (first.CreatorId, repeat.CreatorId));
        Assert.Equal((creator.Id, (Guid?)null), (anonymousFirst.CreatorId, anonymousRepeat.CreatorId));
        Assert.All([first, repeat, anonymousFirst, anonymousRepeat], click => Assert.Equal(postId, click.PostId));
        Assert.Equal(2, await factory.WithDbAsync(db => db.ReferralEvents.CountAsync(referral => referral.PostId == postId, Ct)));
        Assert.Equal(2, (await creator.Client.GetJsonAsync("/api/v1/me/creator/stats", Ct)).GetProperty("clicks").GetInt32());
    }

    // L. A click stored any persona ID it was given; only the caller's own is kept now.
    [Fact]
    public async Task AClickKeepsOnlyTheCallersOwnPersona()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var ownPersona = await owner.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);
        var otherPersona = await other.Client.CreatePersonaAsync("Gece", cancellationToken: Ct);
        var product = await factory.SeedProductAsync(owner.Client, 300m, cancellationToken: Ct);

        var own = await ClickAsync(owner.Client, $"/api/v1/go/{product.OfferId}?src=product&personaId={ownPersona}");
        var forged = await ClickAsync(owner.Client, $"/api/v1/go/{product.OfferId}?src=product&personaId={otherPersona}");

        Assert.Equal(ownPersona, own.PersonaId);
        Assert.Null(forged.PersonaId);
    }

    [Fact]
    public async Task AnOversizedPostUploadIsDeletedWhenProcessingIsRefused()
    {
        using var user = await factory.CreateUserAsync(Ct);
        await user.Client.AcceptConsentAsync(Ct);
        using var created = await user.Client.CreatePostUploadUrlRawAsync(cancellationToken: Ct);
        var postId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("postId").GetGuid();
        var key = (await factory.PostRowAsync(postId, Ct)).ImageOriginalKey!;
        factory.Storage.Upload(key, new byte[(8 * 1024 * 1024) + 1], "image/jpeg");

        using var process = await user.Client.PostAsync($"/api/v1/posts/{postId}/process", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, process.StatusCode);
        Assert.Null(factory.Storage.Find(key));
        Assert.Equal(PostStatus.Processing, (await factory.PostRowAsync(postId, Ct)).Status);
    }

    private async Task<Click> ClickAsync(HttpClient client, string goPath)
    {
        using var response = await client.GetAsync(goPath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var clickId = long.Parse(
            response.Headers.Location!.Query.Split('&').Single(pair => pair.Contains("kombin_click=", StringComparison.Ordinal)).Split('=')[1],
            CultureInfo.InvariantCulture);

        return await factory.WithDbAsync(db => db.Clicks.AsNoTracking().SingleAsync(click => click.Id == clickId, Ct));
    }
}
