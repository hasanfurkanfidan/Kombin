using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The cross-user authorization harness of the security review. <see cref="Cases"/> lists every authenticated route the app maps, keyed
/// <c>"METHOD /route/pattern"</c>, with what happens when user B (the intruder) aims it at user A's (the owner's) resources:
/// <list type="bullet">
/// <item><see cref="Probe"/>: B sends A's identifier; the answer must be the listed status and A's data must be unchanged.</item>
/// <item><see cref="ScopedRead"/>: the route takes no identifier and reads the caller's data; B's answer must not contain any of A's identifiers.</item>
/// <item><see cref="Exempt"/>: the route takes no identifier of another user's private resource (it creates the caller's own data, or acts on
/// a catalog ID, or on a relationship that is open by design); the reason says which.</item>
/// </list>
/// <see cref="EveryAuthenticatedRouteHasACrossUserCase"/> fails when a route is added without an entry (or an entry outlives its route), so a
/// new endpoint cannot ship without someone deciding how another user's ID is refused.
/// </summary>
public sealed class AuthorizationMatrixTests(ApiFactory factory)
{
    private static readonly SemaphoreSlim WorldGate = new(1, 1);

    private static World? SharedWorld;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static readonly IReadOnlyDictionary<string, CrossUserCase> Cases = new Dictionary<string, CrossUserCase>(StringComparer.Ordinal)
    {
        // Identity
        ["GET /api/v1/me"] = ScopedRead("/api/v1/me"),
        ["DELETE /api/v1/me"] = Exempt("Deletes the caller's own account; it takes no user ID."),
        ["POST /api/v1/auth/logout"] = Probe(
            w => Json(HttpMethod.Post, "/api/v1/auth/logout", new { refreshToken = w.OwnerRefreshToken }),
            [HttpStatusCode.NoContent],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.RefreshTokens.AnyAsync(token => token.TokenHash == JwtTokenService.HashRefreshToken(w.OwnerRefreshToken) && token.RevokedAt == null, Ct)))),

        // Engagement
        ["GET /api/v1/me/saved"] = ScopedRead("/api/v1/me/saved"),
        ["GET /api/v1/me/saved/ids"] = ScopedRead("/api/v1/me/saved/ids"),
        ["PUT /api/v1/me/saved/{productId:guid}"] = Exempt("Saves a catalog product (public ID) for the caller."),
        ["DELETE /api/v1/me/saved/{productId:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/saved/{w.ProductId}"),
            [HttpStatusCode.NoContent, HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.SavedProducts.AnyAsync(saved => saved.UserId == w.OwnerId && saved.ProductId == w.ProductId, Ct)))),
        ["GET /api/v1/me/alerts"] = ScopedRead("/api/v1/me/alerts"),
        ["GET /api/v1/me/alerts/triggers"] = ScopedRead("/api/v1/me/alerts/triggers"),
        ["POST /api/v1/me/alerts"] = Exempt("Creates an alert of the caller on a catalog product (public ID)."),
        ["PATCH /api/v1/me/alerts/{id:guid}"] = Probe(
            w => Json(HttpMethod.Patch, $"/api/v1/me/alerts/{w.AlertId}", new { isActive = false }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.PriceAlerts.AnyAsync(alert => alert.Id == w.AlertId && alert.IsActive, Ct)))),
        ["DELETE /api/v1/me/alerts/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/alerts/{w.AlertId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.PriceAlerts.AnyAsync(alert => alert.Id == w.AlertId && alert.IsActive, Ct)))),
        ["GET /api/v1/me/clicks"] = ScopedRead("/api/v1/me/clicks"),

        // Profile
        ["GET /api/v1/me/profile"] = ScopedRead("/api/v1/me/profile"),
        ["PUT /api/v1/me/profile"] = Exempt("Updates the caller's own profile; it takes no ID."),
        ["GET /api/v1/me/personas"] = ScopedRead("/api/v1/me/personas"),
        ["POST /api/v1/me/personas"] = Exempt("Creates a persona of the caller."),
        ["PATCH /api/v1/me/personas/{id:guid}"] = Probe(
            w => Json(HttpMethod.Patch, $"/api/v1/me/personas/{w.OwnerPersonaId}", new { name = "Ele geçirildi" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal("Ofis", await f.WithDbAsync(db => db.Personas.Where(persona => persona.Id == w.OwnerPersonaId).Select(persona => persona.Name).SingleAsync(Ct)))),
        ["DELETE /api/v1/me/personas/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/personas/{w.OwnerPersonaId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.Personas.Where(persona => persona.Id == w.OwnerPersonaId).Select(persona => persona.IsArchived).SingleAsync(Ct)))),
        ["PUT /api/v1/me/personas/{id:guid}/activate"] = Probe(w => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/personas/{w.OwnerPersonaId}/activate"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/personas/{id:guid}/style"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/personas/{w.OwnerPersonaId}/style"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/personas/{id:guid}/style-cards"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/personas/{w.OwnerPersonaId}/style-cards"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/personas/{id:guid}/swipes"] = Probe(
            w => Json(HttpMethod.Post, $"/api/v1/me/personas/{w.OwnerPersonaId}/swipes", new { swipes = new[] { new { styleCardId = ProfileClientExtensions.StyleCards[0].Id, decision = "Like" } } }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.StyleSwipes.AnyAsync(swipe => swipe.PersonaId == w.OwnerPersonaId, Ct)))),
        ["GET /api/v1/me/sizes"] = ScopedRead("/api/v1/me/sizes"),
        ["PUT /api/v1/me/sizes"] = Exempt("Records a size of the caller for a brand (public slug)."),
        ["POST /api/v1/me/sizes/confirm"] = Exempt("Records a size of the caller for a catalog product (public ID)."),
        ["GET /api/v1/me/sizes/hint"] = ScopedRead("/api/v1/me/sizes/hint?productId={product}"),
        ["GET /api/v1/me/size-prompts"] = ScopedRead("/api/v1/me/size-prompts"),
        ["POST /api/v1/me/size-prompts/{id:guid}"] = Probe(
            w => Json(HttpMethod.Post, $"/api/v1/me/size-prompts/{w.SizePromptId}", new { answer = "NeverAsk" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Null(await f.WithDbAsync(db => db.SizePrompts.Where(prompt => prompt.Id == w.SizePromptId).Select(prompt => prompt.Answer).SingleAsync(Ct)))),
        ["DELETE /api/v1/me/sizes/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/sizes/{w.SizeId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.SizeEntries.AnyAsync(entry => entry.Id == w.SizeId, Ct)))),

        // Notification
        ["PUT /api/v1/me/devices"] = Exempt("Registers the caller's phone token. A token moves to whoever registers it last (a phone that switched accounts); Expo tokens are unguessable (review finding L-7)."),
        ["DELETE /api/v1/me/devices/{token}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/devices/{Uri.EscapeDataString(w.OwnerDeviceToken)}"),
            [HttpStatusCode.NoContent],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.Devices.AnyAsync(device => device.ExpoPushToken == w.OwnerDeviceToken && device.UserId == w.OwnerId && device.IsActive, Ct)))),
        ["GET /api/v1/me/notification-preferences"] = ScopedRead("/api/v1/me/notification-preferences"),
        ["PUT /api/v1/me/notification-preferences"] = Exempt("Updates the caller's own preferences; it takes no ID."),
        ["GET /api/v1/me/notifications"] = ScopedRead("/api/v1/me/notifications"),
        ["GET /api/v1/me/notifications/unread-count"] = ScopedRead("/api/v1/me/notifications/unread-count"),
        ["POST /api/v1/me/notifications/read-all"] = Probe(
            _ => new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/notifications/read-all"),
            [HttpStatusCode.NoContent, HttpStatusCode.OK],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.Notifications.AnyAsync(notification => notification.Id == w.NotificationId && notification.ReadAt == null, Ct)))),
        ["POST /api/v1/me/notifications/{id:long}/read"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/notifications/{w.NotificationId}/read"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.Notifications.AnyAsync(notification => notification.Id == w.NotificationId && notification.ReadAt == null, Ct)))),

        // Wardrobe
        ["POST /api/v1/me/wardrobe/items/upload-url"] = Exempt("Creates an item of the caller; the storage key is built by the server."),
        ["POST /api/v1/me/wardrobe/items/from-product"] = Exempt("Creates an item of the caller from a catalog product (public ID)."),

        // Bulk add (task BA2, PR 1: switched off in the test host, so the routes answer 404 BulkDisabled; PR 2 and 3 turn these into real probes).
        ["POST /api/v1/me/wardrobe/bulk/upload-url"] = Exempt("Creates a batch of the caller; the storage key is built by the server."),
        ["POST /api/v1/me/wardrobe/bulk/{id:guid}/process"] = Probe(w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/wardrobe/bulk/{w.ItemId}/process"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/wardrobe/bulk/{id:guid}"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/wardrobe/bulk/{w.ItemId}"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/wardrobe/bulk/{id:guid}/confirm"] = Probe(w => Json(HttpMethod.Post, $"/api/v1/me/wardrobe/bulk/{w.ItemId}/confirm", new { pieces = Array.Empty<object>() }), [HttpStatusCode.NotFound]),
        ["DELETE /api/v1/me/wardrobe/bulk/{id:guid}"] = Probe(w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/wardrobe/bulk/{w.ItemId}"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/wardrobe/items"] = ScopedRead("/api/v1/me/wardrobe/items"),
        ["GET /api/v1/me/wardrobe/stats"] = ScopedRead("/api/v1/me/wardrobe/stats"),
        ["GET /api/v1/me/wardrobe/gaps"] = ScopedRead("/api/v1/me/wardrobe/gaps"),
        ["GET /api/v1/me/wardrobe/summary"] = ScopedRead("/api/v1/me/wardrobe/summary"),
        ["GET /api/v1/me/wardrobe/items/{id:guid}"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/wardrobe/items/{w.ItemId}"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/wardrobe/items/{id:guid}/pairings"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/wardrobe/items/{w.ItemId}/pairings"), [HttpStatusCode.NotFound]),
        ["PATCH /api/v1/me/wardrobe/items/{id:guid}"] = Probe(
            w => Json(HttpMethod.Patch, $"/api/v1/me/wardrobe/items/{w.ItemId}", new { name = "Ele geçirildi" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.NotEqual("Ele geçirildi", (await f.ItemRowAsync(w.ItemId, Ct)).Name)),
        ["DELETE /api/v1/me/wardrobe/items/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/wardrobe/items/{w.ItemId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False((await f.ItemRowAsync(w.ItemId, Ct)).IsArchived)),
        ["POST /api/v1/me/wardrobe/items/{id:guid}/process"] = Probe(w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/wardrobe/items/{w.ItemId}/process"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/wardrobe/items/{id:guid}/wear"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/wardrobe/items/{w.ItemId}/wear"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal(0, (await f.ItemRowAsync(w.ItemId, Ct)).WearCount)),
        ["DELETE /api/v1/me/wardrobe/items/{id:guid}/wear/{date}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/wardrobe/items/{w.ItemId}/wear/2026-01-01"),
            [HttpStatusCode.NotFound]),

        // Recommendation
        ["GET /api/v1/me/outfits/daily"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/outfits/daily?personaId={w.OwnerPersonaId}"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/outfits/how-to-wear"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/outfits/how-to-wear?wardrobeItemId={w.ItemId}"), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/outfits/saved"] = ScopedRead("/api/v1/me/outfits/saved"),
        ["GET /api/v1/me/outfits/{id:guid}"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/outfits/{w.OutfitId}"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/outfits/{id:guid}/save"] = Probe(w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/outfits/{w.OutfitId}/save"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/outfits/{id:guid}/worn"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/outfits/{w.OutfitId}/worn"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Null(await f.WithDbAsync(db => db.Outfits.Where(outfit => outfit.Id == w.OutfitId).Select(outfit => outfit.WornAt).SingleAsync(Ct)))),
        ["POST /api/v1/me/outfits/{id:guid}/feedback"] = Probe(
            w => Json(HttpMethod.Post, $"/api/v1/me/outfits/{w.OutfitId}/feedback", new { action = "Save" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.OutfitFeedback.AnyAsync(feedback => feedback.OutfitId == w.OutfitId, Ct)))),
        ["DELETE /api/v1/me/outfits/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/outfits/{w.OutfitId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.NotEqual(OutfitStatus.Dismissed, await f.WithDbAsync(db => db.Outfits.Where(outfit => outfit.Id == w.OutfitId).Select(outfit => outfit.Status).SingleAsync(Ct)))),

        ["POST /api/v1/me/outfits/budget"] = Probe(w => Json(HttpMethod.Post, "/api/v1/me/outfits/budget", new { budget = 1500, personaId = w.OwnerPersonaId }), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/trips/plan"] = Probe(w => Json(HttpMethod.Post, "/api/v1/me/trips/plan", TripBody(w.OwnerPersonaId)), [HttpStatusCode.NotFound]),
        ["POST /api/v1/me/trips"] = Probe(
            w => Json(HttpMethod.Post, "/api/v1/me/trips", new { name = "Matrix probe", request = TripBody(w.OwnerPersonaId) }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.Trips.AnyAsync(trip => trip.UserId == w.Intruder.Id, Ct)))),
        ["GET /api/v1/me/trips"] = ScopedRead("/api/v1/me/trips"),
        ["GET /api/v1/me/trips/{id:guid}"] = Probe(w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/trips/{w.TripId}"), [HttpStatusCode.NotFound]),
        ["DELETE /api/v1/me/trips/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/trips/{w.TripId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.True(await f.WithDbAsync(db => db.Trips.AnyAsync(trip => trip.Id == w.TripId, Ct)))),
        ["POST /api/v1/me/trips/{id:guid}/replan"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/trips/{w.TripId}/replan"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal("{}", await f.WithDbAsync(db => db.Trips.Where(trip => trip.Id == w.TripId).Select(trip => trip.PlanJson).SingleAsync(Ct)))),

        // Try-on: A's outfit is not B's to draw or to report (the product route takes a public ID and B's own wardrobe only).
        ["GET /api/v1/me/tryon"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/tryon?outfitId={w.OutfitId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.TryOnGarments.AnyAsync(row => row.WardrobeItemId == w.ItemId, Ct)))),
        ["POST /api/v1/me/tryon/reports"] = Probe(
            w => Json(HttpMethod.Post, "/api/v1/me/tryon/reports", new { outfitId = w.OutfitId, garmentIds = new[] { w.ItemId }, renderer = "mannequin-v5" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.TryOnReports.AnyAsync(report => report.OutfitId == w.OutfitId, Ct)))),

        // League
        ["GET /api/v1/league/me"] = ScopedRead("/api/v1/league/me"),
        ["POST /api/v1/league/join"] = Exempt("Joins the caller; it takes no ID."),
        ["POST /api/v1/league/leave"] = Exempt("The caller leaves; it takes no ID."),
        ["DELETE /api/v1/league/me/entry"] = Exempt("Withdraws the caller's own entry of today; it takes no ID."),

        // The caller's next pair, on the world's league day (its own time: a league day closes at 21:00): A's outfit is in the pool, and the
        // answer must carry none of A's identifiers (user, entry, wardrobe item).
        ["GET /api/v1/league/pairs/next"] = Probe(
            w => AtLeagueNoon(new HttpRequestMessage(HttpMethod.Get, "/api/v1/league/pairs/next"), w),
            [HttpStatusCode.OK]),
        ["POST /api/v1/league/pairs/{pairId:guid}/vote"] = Probe(
            w => AtLeagueNoon(Json(HttpMethod.Post, $"/api/v1/league/pairs/{w.LeaguePairId}/vote", new { choice = "left", decisionMs = 1500 }), w),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Null(await f.WithDbAsync(db => db.LeaguePairs.Where(pair => pair.Id == w.LeaguePairId).Select(pair => pair.AnsweredAt).SingleAsync(Ct)))),
        ["POST /api/v1/league/pairs/{pairId:guid}/report"] = Probe(
            w => AtLeagueNoon(Json(HttpMethod.Post, $"/api/v1/league/pairs/{w.LeaguePairId}/report", new { side = "left" }), w),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.LeagueReports.AnyAsync(report => report.ReporterId == w.Intruder.Id, Ct)))),

        // A has a result on a closed day of the world's own; B asks for that day and for B's latest: only the caller's own row is ever read, so
        // both are 404 League.ResultsNotReady and carry none of A's identifiers.
        ["GET /api/v1/league/results"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/league/results?day={w.LeagueResultsDay.Iso()}"),
            [HttpStatusCode.NotFound]),
        // B's own group table this week (B is a player): rows carry only rank, points, isMe and the weekly alias, never A's identifiers.
        ["GET /api/v1/league/table"] = ScopedRead("/api/v1/league/table"),
        ["GET /api/v1/league/results/latest"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Get, "/api/v1/league/results/latest"),
            [HttpStatusCode.NotFound]),

        // Social
        ["GET /api/v1/social/consent"] = ScopedRead("/api/v1/social/consent"),
        ["POST /api/v1/social/consent"] = Exempt("Records the caller's own consent."),
        ["POST /api/v1/posts/upload-url"] = Exempt("Creates a post of the caller; the storage key is built by the server."),
        ["POST /api/v1/posts/from-outfit"] = Probe(
            w => Json(HttpMethod.Post, "/api/v1/posts/from-outfit", new { outfitId = w.OutfitId }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.False(await f.WithDbAsync(db => db.Posts.AnyAsync(post => post.OutfitId == w.OutfitId, Ct)))),
        ["POST /api/v1/posts/{id:guid}/process"] = Probe(w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/posts/{w.DraftPostId}/process"), [HttpStatusCode.NotFound]),
        ["PUT /api/v1/posts/{id:guid}/publish"] = Probe(
            w => Json(HttpMethod.Put, $"/api/v1/posts/{w.DraftPostId}/publish", new { visibility = "Public", tags = Array.Empty<object>() }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal(PostStatus.Draft, (await f.PostRowAsync(w.DraftPostId, Ct)).Status)),
        ["DELETE /api/v1/posts/{id:guid}"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/posts/{w.PrivatePostId}"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal(PostStatus.Live, (await f.PostRowAsync(w.PrivatePostId, Ct)).Status)),
        ["POST /api/v1/posts/{id:guid}/like"] = Probe(
            w => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/posts/{w.FollowersPostId}/like"),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal(0, (await f.PostRowAsync(w.FollowersPostId, Ct)).LikeCount)),
        ["DELETE /api/v1/posts/{id:guid}/like"] = Probe(w => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/posts/{w.PrivatePostId}/like"), [HttpStatusCode.NotFound]),
        ["POST /api/v1/posts/{id:guid}/report"] = Probe(
            w => Json(HttpMethod.Post, $"/api/v1/posts/{w.FollowersPostId}/report", new { reason = "Spam" }),
            [HttpStatusCode.NotFound],
            async (w, f) => Assert.Equal(0, (await f.PostRowAsync(w.FollowersPostId, Ct)).ReportCount)),
        ["POST /api/v1/posts/{id:guid}/try"] = Probe(w => Json(HttpMethod.Post, $"/api/v1/posts/{w.PrivatePostId}/try", new { }), [HttpStatusCode.NotFound]),
        ["GET /api/v1/me/posts"] = ScopedRead("/api/v1/me/posts"),
        ["GET /api/v1/me/blocks"] = ScopedRead("/api/v1/me/blocks"),
        ["GET /api/v1/me/creator/stats"] = Probe(_ => new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/creator/stats"), [HttpStatusCode.Forbidden]),
        ["POST /api/v1/users/{id:guid}/follow"] = Exempt("Following is open by design (no follow requests); it is what makes followers-only posts visible (review finding M-5)."),
        ["DELETE /api/v1/users/{id:guid}/follow"] = Exempt("Removes the caller's own follow row only."),
        ["POST /api/v1/users/{id:guid}/block"] = Exempt("Adds the caller's own block row; blocking anyone is allowed."),
        ["DELETE /api/v1/users/{id:guid}/block"] = Exempt("Removes the caller's own block row only."),
    };

    /// <summary>Routes that work signed in or out but take an identifier the caller must own, or must be allowed to see.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<World, HttpRequestMessage>> OptionalAuthProbes = new Dictionary<string, Func<World, HttpRequestMessage>>(StringComparer.Ordinal)
    {
        ["private post"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/posts/{w.PrivatePostId}"),
        ["followers-only post, not following"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/posts/{w.FollowersPostId}"),
        ["draft post"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/posts/{w.DraftPostId}"),
        ["catalog list with someone else's persona"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/catalog/products?personaId={w.OwnerPersonaId}"),
        ["product with someone else's persona"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/catalog/products/{w.ProductId}?personaId={w.OwnerPersonaId}"),
        ["feed with someone else's persona"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/feed?personaId={w.OwnerPersonaId}"),
        ["a user's posts with someone else's persona"] = w => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/{w.OwnerId}/posts?personaId={w.OwnerPersonaId}"),
    };

    public static TheoryData<string> CaseKeys => [.. Cases.Keys.Order(StringComparer.Ordinal)];

    public static TheoryData<string> OptionalAuthKeys => [.. OptionalAuthProbes.Keys];

    [Fact]
    public void EveryAuthenticatedRouteHasACrossUserCase()
    {
        var authenticated = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ActionDescriptor>() is null)
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(Keys)
            .ToHashSet(StringComparer.Ordinal);

        var missing = authenticated.Except(Cases.Keys).Order(StringComparer.Ordinal).ToList();
        var stale = Cases.Keys.Except(authenticated).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "Authenticated routes without a cross-user case in AuthorizationMatrixTests.Cases:\n" + string.Join('\n', missing));
        Assert.True(stale.Count == 0, "Cases for routes that are not authenticated (or no longer exist):\n" + string.Join('\n', stale));
    }

    [Theory]
    [MemberData(nameof(CaseKeys))]
    public async Task AnotherUsersIdentifierIsRefused(string route)
    {
        var world = await WorldAsync();
        var entry = Cases[route];

        switch (entry)
        {
            case ProbeCase probe:
                {
                    using var request = probe.Request(world);
                    using var response = await world.Intruder.Client.SendAsync(request, Ct);
                    var body = await response.Content.ReadAsStringAsync(Ct);
                    Assert.True(probe.Expected.Contains(response.StatusCode), $"{route}: {(int)response.StatusCode} {body}");
                    AssertNoOwnerData(world, route, body);

                    if (probe.Unchanged is not null)
                    {
                        await probe.Unchanged(world, factory);
                    }

                    break;
                }

            case ScopedReadCase read:
                {
                    using var response = await world.Intruder.Client.GetAsync(read.Path.Replace("{product}", world.ProductId.ToString(), StringComparison.Ordinal), Ct);
                    var body = await response.Content.ReadAsStringAsync(Ct);
                    Assert.True(response.IsSuccessStatusCode, $"{route}: {(int)response.StatusCode} {body}");
                    AssertNoOwnerData(world, route, body);
                    break;
                }

            case ExemptCase exempt:
                Assert.False(string.IsNullOrWhiteSpace(exempt.Reason));
                break;
        }
    }

    [Theory]
    [MemberData(nameof(OptionalAuthKeys))]
    public async Task OptionalAuthRoutesRefuseWhatTheCallerMayNotSee(string name)
    {
        var world = await WorldAsync();

        using var signedIn = OptionalAuthProbes[name](world);
        using var asIntruder = await world.Intruder.Client.SendAsync(signedIn, Ct);
        Assert.Equal(HttpStatusCode.NotFound, asIntruder.StatusCode);

        // Anonymous: a persona needs a sign-in (400), a hidden post is simply not there (404).
        using var anonymousRequest = OptionalAuthProbes[name](world);
        using var anonymous = factory.CreateAnonymousClient();
        using var asAnonymous = await anonymous.SendAsync(anonymousRequest, Ct);
        Assert.Contains(asAnonymous.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task ListsNeverShowAnotherUsersHiddenPosts()
    {
        var world = await WorldAsync();
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var client in new[] { world.Intruder.Client, anonymous })
        {
            foreach (var path in new[] { $"/api/v1/users/{world.OwnerId}/posts", "/api/v1/feed", "/api/v1/feed?mode=forYou&page=1" })
            {
                using var response = await client.GetAsync(path, Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
                Assert.DoesNotContain(world.PrivatePostId.ToString(), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(world.FollowersPostId.ToString(), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(world.DraftPostId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            }
        }

        var profile = await world.Intruder.Client.GetJsonAsync($"/api/v1/users/{world.OwnerId}", Ct);
        Assert.Equal(0, profile.GetProperty("postCount").GetInt32());
        Assert.DoesNotContain(world.OwnerEmail, profile.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryInternalRouteRefusesACallWithoutTheKey()
    {
        using var client = factory.CreateAnonymousClient();
        var routes = Endpoints().Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/internal/", StringComparison.Ordinal)).SelectMany(Keys).ToList();
        Assert.NotEmpty(routes);

        foreach (var route in routes)
        {
            var (method, pattern) = (route[..route.IndexOf(' ', StringComparison.Ordinal)], route[(route.IndexOf(' ', StringComparison.Ordinal) + 1)..]);
            var url = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}:]+:guid\}", Guid.NewGuid().ToString());
            url = System.Text.RegularExpressions.Regex.Replace(url, @"\{[^}]+\}", "probe");

            foreach (var key in new[] { null, string.Empty, "wrong-key-0123456789abcdef0123456789" })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (method is "POST" or "PUT")
                {
                    request.Content = JsonContent.Create(new { });
                }

                if (key is not null)
                {
                    request.Headers.Add(InternalKeyFilter.HeaderName, key);
                }

                using var response = await client.SendAsync(request, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{route} with key '{key}': {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task EveryAdminPageButTheLoginNeedsTheAdminSignIn()
    {
        using var client = factory.CreateAnonymousClient();
        var appUser = await factory.CreateUserAsync(Ct);
        var pages = Endpoints()
            .Select(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/'))
            .Where(path => path.StartsWith("admin", StringComparison.Ordinal) && path != "admin/login")
            .Distinct()
            .ToList();
        Assert.Contains("admin/users", pages);
        Assert.Contains("admin/league", pages);

        foreach (var page in pages)
        {
            foreach (var caller in new[] { client, appUser.Client })
            {
                using var response = await caller.GetAsync("/" + page, Ct);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.ToString().Contains("/admin/login", StringComparison.Ordinal),
                    $"/{page}: {(int)response.StatusCode} {response.Headers.Location}");
            }
        }

        appUser.Dispose();
    }

    private static void AssertNoOwnerData(World world, string route, string body)
    {
        foreach (var secret in world.OwnerSecrets)
        {
            Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{route} answered user B with user A's '{secret}': {body}");
        }
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static IEnumerable<string> Keys(RouteEndpoint endpoint)
    {
        var pattern = endpoint.RoutePattern.RawText!;
        var path = (pattern.Length > 1 ? pattern.TrimEnd('/') : pattern).Insert(0, pattern.StartsWith('/') ? string.Empty : "/");
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

        return methods.Select(method => $"{method} {path}");
    }

    private static ProbeCase Probe(Func<World, HttpRequestMessage> request, HttpStatusCode[] expected, Func<World, ApiFactory, Task>? unchanged = null) =>
        new ProbeCase(request, expected, unchanged);

    private static ScopedReadCase ScopedRead(string path) => new ScopedReadCase(path);

    private static ExemptCase Exempt(string reason) => new ExemptCase(reason);

    private static HttpRequestMessage Json(HttpMethod method, string url, object body) => new(method, url) { Content = JsonContent.Create(body) };

    /// <summary>The request runs at noon of the world's league day (the test clock's header).</summary>
    private static HttpRequestMessage AtLeagueNoon(HttpRequestMessage request, World world)
    {
        request.Headers.Add(TestHeaderClock.Header, LeagueClientExtensions.Time(world.LeagueDay, 12).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return request;
    }

    /// <summary>A valid trip plan request (two days from the day after tomorrow) naming <paramref name="personaId"/>.</summary>
    private static object TripBody(Guid personaId)
    {
        var start = DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime).AddDays(2);
        return new { city = "Istanbul", startDate = start, endDate = start.AddDays(1), personaId };
    }

    private async Task<World> WorldAsync()
    {
        await WorldGate.WaitAsync(Ct);

        try
        {
            return SharedWorld ??= await BuildWorldAsync();
        }
        finally
        {
            WorldGate.Release();
        }
    }

    /// <summary>User A with one of everything; user B with a persona of their own (so a request is refused for A's ID, not for B lacking one).</summary>
    private async Task<World> BuildWorldAsync()
    {
        var cancellation = CancellationToken.None;
        using var anonymous = factory.CreateAnonymousClient();
        var owner = await factory.CreateUserAsync(cancellation);
        var intruder = await factory.CreateUserAsync(cancellation);
        var ownerEmail = (await owner.Client.GetJsonAsync("/api/v1/me", cancellation)).GetProperty("email").GetString()!;

        var ownerPersona = await owner.Client.CreatePersonaAsync("Ofis", cancellationToken: cancellation);
        await intruder.Client.CreatePersonaAsync("Ofis", cancellationToken: cancellation);
        await intruder.Client.AcceptConsentAsync(cancellation);
        var itemId = await factory.AddPhotoItemAsync(owner.Client, cancellationToken: cancellation);
        var product = await factory.SeedProductAsync(anonymous, 400m, cancellationToken: cancellation);
        var alertId = await owner.Client.CreateAlertOkAsync(product.ProductId, "PriceDrop", targetPrice: 300m, cancellationToken: cancellation);
        using (var save = await owner.Client.PutAsync($"/api/v1/me/saved/{product.ProductId}", null, cancellation))
        {
            Assert.True(save.IsSuccessStatusCode);
        }

        var sizeId = await factory.WithDbAsync(async db =>
        {
            var brandId = await db.Products.Where(candidate => candidate.Id == product.ProductId).Select(candidate => candidate.BrandId).SingleAsync(cancellation);
            var entry = new SizeEntry { UserId = owner.Id, BrandId = brandId, Group = SizeGroup.Outerwear, Size = "M" };
            db.SizeEntries.Add(entry);
            await db.SaveChangesAsync(cancellation);
            return entry.Id;
        });
        var sizePromptId = await factory.WithDbAsync(async db =>
        {
            var prompt = new SizePrompt
            {
                UserId = owner.Id,
                ProductId = product.ProductId,
                SellerId = await db.Offers.Where(candidate => candidate.Id == product.OfferId).Select(candidate => candidate.SellerId).SingleAsync(cancellation),
                OfferId = product.OfferId,
                ClickId = 1,
                ClickedAt = TestClock.UtcNow.AddDays(-5),
            };
            db.SizePrompts.Add(prompt);
            await db.SaveChangesAsync(cancellation);
            return prompt.Id;
        });
        var outfitId = await factory.WithDbAsync(async db =>
        {
            var outfit = new Outfit
            {
                UserId = owner.Id,
                PersonaId = ownerPersona,
                Kind = OutfitKind.Daily,
                Completeness = OutfitCompleteness.Complete,
                ForDate = new DateOnly(2026, 1, 1),
                Rule = "matrix",
                Hash = Guid.NewGuid().ToString("N"),
                Status = OutfitStatus.Saved,
                Narration = "Matrix",
                NarrationInput = "{}",
                SavedAt = TestClock.UtcNow,
            };
            db.Outfits.Add(outfit);
            await db.SaveChangesAsync(cancellation);
            return outfit.Id;
        });
        var notificationId = await factory.InsertNotificationAsync(owner.Id, Kombin.Api.Persistence.Entities.Notification.NotificationStatus.Sent, sentAt: TestClock.UtcNow, cancellationToken: cancellation);
        var deviceToken = await owner.Client.RegisterDeviceAsync(cancellationToken: cancellation);
        var privatePost = await factory.CreateLivePostAsync(owner.Client, "Private", cancellationToken: cancellation);
        var followersPost = await factory.CreateLivePostAsync(owner.Client, "Followers", cancellationToken: cancellation);
        var draftPost = await factory.CreateProcessedPostAsync(owner.Client, cancellationToken: cancellation);

        var tripId = await factory.WithDbAsync(async db =>
        {
            var trip = new Trip
            {
                UserId = owner.Id,
                Name = "Matrix",
                City = "Istanbul",
                StartDate = new DateOnly(2030, 1, 1),
                EndDate = new DateOnly(2030, 1, 2),
                RequestJson = "{}",
                PlanJson = "{}",
                PlannedAt = TestClock.UtcNow,
            };
            db.Trips.Add(trip);
            await db.SaveChangesAsync(cancellation);
            return trip.Id;
        });

        // Kombin Ligi on a day of the world's own: A plays (an entry showing A's wardrobe piece, and an open pair of two engine outfits A was
        // served); B is a player too, so B's next pair is A's outfit against an engine one.
        var leagueDay = LeagueClientExtensions.NewDay();
        await owner.Client.JoinLeagueOkAsync(cancellation);
        await intruder.Client.JoinLeagueOkAsync(cancellation);
        var leagueEntryId = await factory.InsertEntryAsync(leagueDay, owner.Id, cancellationToken: cancellation);
        await factory.WithDbAsync(db => db.LeagueEntryPieces.Where(piece => piece.EntryId == leagueEntryId && piece.Position == 0)
            .ExecuteUpdateAsync(set => set.SetProperty(piece => piece.WardrobeItemId, itemId), cancellation));
        var engineLeft = await factory.InsertEntryAsync(leagueDay, null, cancellationToken: cancellation);
        var engineRight = await factory.InsertEntryAsync(leagueDay, null, cancellationToken: cancellation);
        var leaguePairId = await factory.WithDbAsync(async db =>
        {
            var pair = new Kombin.Api.Persistence.Entities.League.LeaguePair
            {
                Day = leagueDay,
                VoterId = owner.Id,
                LeftEntryId = engineLeft,
                RightEntryId = engineRight,
                LowEntryId = engineLeft.CompareTo(engineRight) < 0 ? engineLeft : engineRight,
                HighEntryId = engineLeft.CompareTo(engineRight) < 0 ? engineRight : engineLeft,
                MatchLevel = Kombin.Api.Persistence.Entities.League.LeaguePairMatch.Strict,
                Sequence = 1,
                ServedAt = LeagueClientExtensions.Time(leagueDay, 11),
            };
            db.LeaguePairs.Add(pair);
            await db.SaveChangesAsync(cancellation);
            return pair.Id;
        });

        // A's result on a closed league day (written as the close writes it), with A's entry of that day.
        var resultsDay = LeagueClientExtensions.NewDay();
        var resultsEntryId = await factory.InsertEntryAsync(resultsDay, owner.Id, cancellationToken: cancellation);
        await factory.WithDbAsync(async db =>
        {
            db.LeagueDays.Add(new Kombin.Api.Persistence.Entities.League.LeagueDay { Day = resultsDay, ClosesAt = LeagueClientExtensions.Time(resultsDay, 21), ClosedAt = LeagueClientExtensions.Time(resultsDay, 21, 1) });
            db.LeagueDailyScores.Add(new Kombin.Api.Persistence.Entities.League.LeagueDailyScore
            {
                UserId = owner.Id,
                Day = resultsDay,
                EntryId = resultsEntryId,
                Competed = true,
                Ranked = true,
                Rank = 1,
                RankedCount = 1,
                Comparisons = 8,
                Percentile = 0.5,
                VotePoints = 35,
                Total = 35,
            });
            return await db.SaveChangesAsync(cancellation);
        });

        // A second session of the owner, whose refresh token the intruder tries to log out.
        using var login = await anonymous.LoginAsync(ownerEmail, ApiClientExtensions.DefaultPassword, cancellationToken: cancellation);
        var ownerRefresh = (await login.ReadTokensAsync(cancellation)).RefreshToken;

        return new World(
            owner.Id, ownerEmail, intruder, ownerPersona, itemId, product.ProductId, alertId, sizeId, sizePromptId, outfitId, notificationId, deviceToken,
            privatePost, followersPost, draftPost, ownerRefresh, tripId, leagueDay, leagueEntryId, leaguePairId, resultsDay, resultsEntryId);
    }

    internal sealed record World(
        Guid OwnerId,
        string OwnerEmail,
        TestUser Intruder,
        Guid OwnerPersonaId,
        Guid ItemId,
        Guid ProductId,
        Guid AlertId,
        Guid SizeId,
        Guid SizePromptId,
        Guid OutfitId,
        long NotificationId,
        string OwnerDeviceToken,
        Guid PrivatePostId,
        Guid FollowersPostId,
        Guid DraftPostId,
        string OwnerRefreshToken,
        Guid TripId,
        DateOnly LeagueDay,
        Guid LeagueEntryId,
        Guid LeaguePairId,
        DateOnly LeagueResultsDay,
        Guid LeagueResultsEntryId)
    {
        /// <summary>What must never appear in an answer to user B. The product is public and the notification ID numeric, so neither is listed.</summary>
        public IReadOnlyList<string> OwnerSecrets =>
        [
            OwnerId.ToString(), OwnerEmail, OwnerPersonaId.ToString(), ItemId.ToString(), AlertId.ToString(), SizeId.ToString(), SizePromptId.ToString(), OutfitId.ToString(),
            OwnerDeviceToken, PrivatePostId.ToString(), FollowersPostId.ToString(), DraftPostId.ToString(), TripId.ToString(),
            LeagueEntryId.ToString(), LeaguePairId.ToString(), LeagueResultsEntryId.ToString(),
        ];
    }

    internal abstract record CrossUserCase;

    internal sealed record ProbeCase(Func<World, HttpRequestMessage> Request, HttpStatusCode[] Expected, Func<World, ApiFactory, Task>? Unchanged) : CrossUserCase;

    internal sealed record ScopedReadCase(string Path) : CrossUserCase;

    internal sealed record ExemptCase(string Reason) : CrossUserCase;
}
