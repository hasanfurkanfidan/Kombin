# Security review S1 (pre-launch)

Branch `task-s1-security-review`, November 2026. A defensive review of our own code before strangers look at it: every route mapped from the
code, each area below read in full, each Critical/High finding proven by an integration test that failed before its fix. Findings are
things that are true of this code, with where, how, and what was done.

**Counts:** Critical 0 · High 2 · Medium 6 · Low 9 · Info 13 (I13 added with Task P1). **Fixed in this branch:** both Highs, five Mediums (M1, M3, M4, M6 in full, M2
in part: oversized uploads are deleted), one Low (L1). M2's size-limited upload ships with the mobile change before the public launch; M5 is
accepted and documented (CEO decisions of 2026-11-15). Everything else is planned or accepted below, with the reason.

## 1. What an attacker would try first

1. **Take over an account before its owner arrives** (H1). Registration does not verify the e-mail address. Register `victim@gmail.com` with
   a password of your choice, wait for the victim to tap "Google ile devam et" (we linked by verified e-mail), and log in with your password
   to read their wardrobe photos, measurements and posts. *Fixed:* linking by e-mail now ends the password and every session made with it.
2. **Make the server fetch an internal address** (H2). Catalog image URLs come from scraped pages. A product image of
   `http://169.254.169.254/...`, `http://minio:9000/...` or a public URL that redirects there was downloaded from inside the compose network;
   "Dolabıma ekle" then stored what came back as a **public** `cutout`/`thumb` object. *Fixed:* `OutboundGuard`.
3. **Walk IDs.** Every authenticated route was tried with user B's token against user A's resources (the harness in §4). Nothing leaked:
   every handler filters by the caller, every visibility check goes through `PostAccess`/`PostRules.CanView`. This is the result most worth
   keeping, which is why the harness now fails the build when a new route arrives without a case.
4. **Farm creator earnings.** Reloading a creator's tag link (signed out, a script, or the creator from a second device) earned a click each
   time. *Fixed:* one viewer earns a creator once per post and product a day.
5. **Hide someone's post with three throwaway accounts** (M4), **fill the disk** through presigned uploads (M2) or the analytics intake (M6).
   *Fixed:* M4 (only trusted reporters hide) and M6 (a daily cap per actor, 180-day retention); M2 is limited (oversized uploads are deleted
   at once) until the size-limited upload ships with the mobile change.

## 2. Attack surface (generated from the code)

Sources: every `Map{Get,Post,Put,Patch,Delete}` under `src/Kombin.Api/Features`, the Razor pages under `Features/Admin/Pages` and
`Features/Legal/Pages`, `Common/Storage`, `docker/caddy/Caddyfile`. `AuthorizationMatrixTests.EveryAuthenticatedRouteHasACrossUserCase`
enumerates the same list from the running app's `EndpointDataSource`, so this table cannot silently drift from the authenticated part of it.

Legend: **Anon** = anonymous allowed; **User** = JWT required (`ICurrentUser.UserId`); **Opt** = works signed in or out
(`UserIdOrNull`); **Key** = `X-Internal-Key` (constant-time SHA-256 compare, `InternalKeyFilter`; Caddy also limits `/api/v1/internal/*` to
`INTERNAL_ALLOWED_IPS`); **Admin** = `KombinAdmin` cookie (Caddy also limits `/admin` to `ADMIN_ALLOWED_IPS`). "Ids from client" lists what
the caller chooses; every one of them is checked against the caller where it is private.

### Public and optional-auth

| Route | Auth | Ids from client | Reads / writes | Rate limit |
|---|---|---|---|---|
| `POST /api/v1/auth/register` | Anon | — | creates user, refresh token | `auth` |
| `POST /api/v1/auth/login`, `/external`, `/refresh`, `/cancel-deletion` | Anon | e-mail / ID token / refresh token | users, refresh tokens, analytics link | `auth` |
| `GET /api/v1/catalog/categories`, `/brands?q=`, `/products?q=&personaId=&sort=`, `/products/{id}?personaId=`, `/products/{id}/price-history` | Opt | product id, persona id (must be the caller's: 400 anonymous, 404 foreign) | catalog | global |
| `GET /api/v1/go/{offerId}?src=&personaId=&post=` | Opt | offer, persona (kept only if the caller's), post (attribution only if live, not private and tags the product) | writes `clicks`, `referral_events`; 302 to the **stored** offer URL | `go` |
| `POST /api/v1/events` | Opt | `anonymousId` | writes `analytics.events` (allow-listed) | `events` |
| `POST /api/v1/lens/search?audience=` | Opt | — (photo in memory only) | catalog embeddings; caller's own wardrobe | `lens` |
| `POST /api/v1/outfits/budget` | Anon (a token is ignored) | — | catalog only, nothing written | `budget` |
| `GET /api/v1/feed?mode=&personaId=&page=` | Opt | persona | live public posts (+ followed authors' followers-only posts) | global |
| `GET /api/v1/posts/{id}?personaId=` | Opt | post, persona | `PostAccess.FindVisibleAsync` | global |
| `GET /api/v1/users/{id}`, `/users/{id}/posts` | Opt | user, persona | profile summary (no e-mail), visible posts | global |
| `GET /api/v1/creators/{handle}` | Opt | handle | creator profile, visible posts | global |
| `GET /api/v1/legal`, `/legal/privacy`, `/legal/terms`, `/legal/delete-account` | Anon | — | static pages | global |
| `GET /health`, `/health/live` | Anon | — | PostgreSQL ping | exempt |

### Authenticated (`/api/v1/me/...` unless noted; all scoped to the caller)

| Area | Routes | Ids from client |
|---|---|---|
| Identity | `GET /me`, `DELETE /me`, `POST /auth/logout` | refresh token (revoked only if the caller's) |
| Engagement | `GET/PUT/DELETE /saved[/ids|/{productId}]`, `GET/POST /alerts`, `PATCH/DELETE /alerts/{id}`, `GET /alerts/triggers`, `GET /clicks` | product (public), alert |
| Profile | `GET/PUT /profile`, `GET/POST /personas`, `PATCH/DELETE /personas/{id}`, `PUT /personas/{id}/activate`, `GET /personas/{id}/style`, `GET /personas/{id}/style-cards`, `POST /personas/{id}/swipes`, `GET/PUT /sizes`, `POST /sizes/confirm`, `GET /sizes/hint`, `DELETE /sizes/{id}` | persona, size entry, product/brand (public) |
| Notification | `PUT /devices`, `DELETE /devices/{token}`, `GET/PUT /notification-preferences`, `GET /notifications`, `GET /notifications/unread-count`, `POST /notifications/read-all`, `POST /notifications/{id}/read` | push token, notification |
| Wardrobe | `POST /wardrobe/items/upload-url`, `POST /wardrobe/items/from-product`, `GET /wardrobe/items`, `GET/PATCH/DELETE /wardrobe/items/{id}`, `POST /wardrobe/items/{id}/process`, `POST /wardrobe/items/{id}/wear`, `DELETE /wardrobe/items/{id}/wear/{date}`, `GET /wardrobe/items/{id}/pairings`, `GET /wardrobe/stats`, `GET /wardrobe/gaps` | item, product (public) |
| Recommendation | `GET /outfits/daily`, `GET /outfits/how-to-wear`, `GET /outfits/saved`, `GET/DELETE /outfits/{id}`, `POST /outfits/{id}/save|worn|feedback`, `POST /outfits/budget` (`budget`), `POST /trips/plan`, `POST /trips` (`trips`), `GET /trips`, `GET/DELETE /trips/{id}`, `POST /trips/{id}/replan` (`trips`) | outfit, wardrobe item, persona, trip |
| Social | `GET/POST /api/v1/social/consent`, `POST /api/v1/posts/upload-url`, `POST /api/v1/posts/from-outfit`, `POST /api/v1/posts/{id}/process`, `PUT /api/v1/posts/{id}/publish`, `DELETE /api/v1/posts/{id}`, `POST/DELETE /api/v1/posts/{id}/like`, `POST /api/v1/posts/{id}/report`, `POST /api/v1/posts/{id}/try`, `GET /me/posts`, `GET /me/blocks`, `GET /me/creator/stats`, `POST/DELETE /api/v1/users/{id}/follow`, `POST/DELETE /api/v1/users/{id}/block` | post, outfit, wardrobe items in tags (must be the author's), user |
| League (`/api/v1/league/...`, tasks L2, L3 and L4; off unless `League:Enabled`, then 404 `League.Disabled`) | `GET /me`, `POST /join`, `POST /leave`, `DELETE /me/entry`, `GET /pairs/next`, `POST /pairs/{pairId}/vote`, `POST /pairs/{pairId}/report` (these three: `league`), `GET /results?day=`, `GET /results/latest` | pair (must be the caller's: 404 `League.PairNotFound` otherwise); results read the caller's own row only (another player's day is 404 `League.ResultsNotReady`); answers carry no other player's identifier (`LeagueAnonymityTests`) |
| League internal (task L4) | `POST /api/v1/internal/league/days/{date}/close` (internal key, then the league switch) | closes a day with the hourly job's code; one close at a time (advisory lock, 409 `League.CloseInProgress`), never before the day's 21:00 (409 `League.DayNotOver`) |

### Internal (`X-Internal-Key`)

`POST /internal/catalog/offers/upsert`, `POST /internal/catalog/offers/sweep`, `GET /internal/catalog/sellers/{sellerSlug}/offers`,
`PUT /internal/catalog/sellers/{slug}`, `POST /internal/catalog/embeddings/run`, `POST /internal/notifications/test`,
`GET /internal/analytics/weekly`, `PUT|DELETE /internal/social/creators/{userId}`, `GET /internal/social/review-queue`,
`POST /internal/social/posts/{id}/review`, `POST /internal/identity/users/{userId}/disable|enable` (all under `/api/v1`).
`AuthorizationMatrixTests.EveryInternalRouteRefusesACallWithoutTheKey` calls each with no key, an empty key and a wrong key: always 401.

### Admin (Razor, `KombinAdmin` cookie)

`/admin` (dashboard), `/admin/review`, `/admin/creators`, `/admin/catalog`, `/admin/users`, `/admin/audit`, `/admin/logout`, and the
anonymous `/admin/login`. Every POST validates the antiforgery token (Razor Pages default); no GET handler changes state (`OnGet*` only read;
`/admin/logout` GET only redirects). `EveryAdminPageButTheLoginNeedsTheAdminSignIn` checks each page anonymously and with an app user's JWT.
`/hangfire` is mapped only in Development or with `Diagnostics:Expose` (then behind the admin cookie); Caddy blocks it, `/openapi` and
`/internal/*` outright.

### Storage

Keys are server-built `{area}/{userId}/{ownerId}/{variant}.{ext}` (`StorageKeys.Build`; the client never chooses one). Presigned PUT: one key,
`Content-Type` signed, 15 min, **no size cap** (M2). Presigned GET: 1 h for post images, 15 min for wardrobe originals. Public by bucket policy:
`*/cutout.*` and `*/thumb.*` only (`docker/minio/public-read.json`); everything else, originals and post renditions included, is private.
Keys contain two random GUIDs (not guessable); public ones are disclosed wherever the image is shown (wardrobe tags on posts).
The first GUID is the owner's user ID, so a public key links an image to an account: Kombin Ligi, whose responses carry no name, no profile and no stable identifier, will therefore
show copies under `league/{random}/{random}/thumb.*` instead of the wardrobe keys (`docs/design/league.md`, section 4; built in task L3 by
`EntryMaterializer`, new keys every day, deleted within minutes of a withdrawal or a leave). Since task L3-fix every copy also differs in bytes
(`ImageMarker`: a random block in a part of the file decoders skip), so neither a content hash nor the store's ETag links two copies of one piece
or a copy to the wardrobe thumbnail a Social post shows; the pixels are the same, so a person or a perceptual hash can still recognise the
photo (accepted, `docs/privacy-notes.md` 6b; consent version `2026-11-v2` tells the player: "Parçalarını tanıyan biri kombinin sana ait olduğunu
anlayabilir."). Account deletion finds those copies through the user's entries (`AccountPurger`).

## 3. Findings

Sorted by severity. Status: **Fixed** (in this branch, with the test that proves it), **Planned** (concrete plan), **Accepted** (with reason).

| ID | Severity | Title | Where | Status |
|---|---|---|---|---|
| H1 | High | Account pre-hijacking through Google/Apple link-by-email | `Features/Identity/ExternalSignIn/Endpoint.cs:115` | Fixed |
| H2 | High | SSRF in catalog image downloads (embedding job, "Dolabıma ekle"), readable through a public object | `Features/Catalog/CatalogModule.cs:36`, `Features/Wardrobe/WardrobeModule.cs:30`, `Common/Http/OutboundGuard.cs` | Fixed |
| M1 | Medium | Creator click inflation: every repeat click earned a referral | `Features/Engagement/Redirect/Endpoint.cs:72`, `Features/Social/CreatorAttribution.cs` | Fixed |
| M2 | Medium | Presigned PUT has no size cap: storage fill | `Common/Storage/S3ObjectStorage.cs:87` | Partly fixed; rest planned with the mobile upload change, before public launch |
| M3 | Medium | Wardrobe-tag thumbnails bypass moderation | `Features/Social/PostResponses.cs:203` | Fixed |
| M4 | Medium | Three throwaway accounts hide any live post | `Features/Social/ReportPost/Endpoint.cs` | Fixed |
| M5 | Medium | "Followers" visibility is not a privacy boundary | `Features/Social/PostRules.cs:44` | Accepted; API docs say so, the app rewords it |
| M6 | Medium | Analytics intake as an anonymous storage-fill vector | `Features/Analytics/RecordEvents/Endpoint.cs` | Fixed |
| L1 | Low | `/go` stored any persona ID | `Features/Engagement/Redirect/Endpoint.cs` | Fixed |
| L2 | Low | Registration reveals whether an e-mail has an account | `Features/Identity/Register/Endpoint.cs:40` | Accepted |
| L3 | Low | A replayed rotated refresh token signs the user out everywhere, repeatedly | `Features/Identity/RefreshToken/Endpoint.cs:46` | Planned |
| L4 | Low | Content created in the 15-minute access-token window after `DELETE /me` stays live | `Features/Social/CreateUploadUrl/Endpoint.cs:21` | Planned |
| L5 | Low | Captions and tag labels are unmoderated public text | `Features/Social/PublishPost/Endpoint.cs:105` | Planned |
| L6 | Low | One internal key for workers and founder actions | `Features/*/…Module.cs` (`RequireInternalKey`) | Planned |
| L7 | Low | A push token moves to whoever registers it last | `Features/Notification/RegisterDevice/Endpoint.cs:73` | Accepted |
| L8 | Low | Expensive anonymous reads only behind the global limit | `Features/Social/GetFeed/Endpoint.cs:41`, `Features/Catalog/ListProducts/Endpoint.cs:156` | Planned |
| L9 | Low | Development compose publishes MinIO and the API on every interface with default MinIO credentials | `docker/compose.yaml:42`, `:105` | Accepted (development only) |
| I1–I12 | Info | See §3.4 | | |

### 3.1 High

**H1 · Account pre-hijacking through link-by-email.** `ExternalSignInRules.Decide` links a Google/Apple identity to an existing account whose
e-mail matches the token's verified e-mail. That is safe for the *provider's* side, but registration (`POST /auth/register`) never verifies the
address, so the password on that account may belong to someone else. *Exploit:* register `victim@gmail.com` with your password; when the victim
first signs in with Google they land in your account, keep using it (wardrobe photos, measurements, posts), and you sign in with the password
whenever you like. *Fix:* when a sign-in links by e-mail to an account that has a password, the password is cleared and every refresh token of
the account is **deleted** (not revoked: replaying a revoked token triggers reuse detection, which would let the attacker sign the victim out
again and again, see L3), all in the sign-in's own `SaveChanges`. The owner of the address is the only one left signed in. Cost: a user who
registered with a password and later taps Google loses the password way in; Google/Apple remain (there is no password reset to lose).
*Proof:* `SecurityReviewTests.AGoogleSignInThatClaimsAPasswordAccountByEmailEndsThePasswordAndItsSessions`; the old expectation
(`APasswordUserLinksGoogleByEmailAndHasTwoWaysIn`) became `…AndGoogleBecomesTheWayIn`. Access tokens already issued still work until they
expire (≤ 15 min), as everywhere (I4).

**H2 · SSRF in catalog image downloads.** `product-embeddings` (`CatalogImageFetcher`) and `POST /me/wardrobe/items/from-product`
(`CatalogImages.DownloadAsync`) downloaded any http(s) URL the upsert accepted (`IsPublicHttpUrl` checks the shape, not the address), from
inside the compose network, following up to 50 redirects. The URLs come from pages the workers scrape, i.e. from third parties. *Exploit:* a
product image of `http://169.254.169.254/latest/meta-data/...` (cloud metadata), `http://minio:9000/...`, `http://vision:8090/...`, or a public
URL redirecting there; the wardrobe copy then stores any `image/*` answer as `*/cutout.*` and `*/thumb.*`, which the bucket policy serves to
anyone, a full-read SSRF. *Fix:* `Common/Http/OutboundGuard.cs`, applied to both named clients with `.AddPublicOnlyGuard()`: http(s) only, no
credentials, every host resolved and refused unless **all** its addresses are public (loopback, RFC 1918, link-local/metadata, CGNAT,
multicast, reserved, IPv6 ULA/link-local and IPv4 embedded in IPv4-mapped, NAT64 and 6to4 addresses), redirects followed by the guard itself
(at most 3, each target checked), and a socket handler whose `ConnectCallback` resolves again and connects only to a vetted address (so DNS
rebinding between the check and the connect still fails). A refused fetch is `Unusable` (`image_refused_address`), not retried. Size (8 MB,
counted while reading) and content type checks were already there. *Proof:* `SecurityReviewTests.TheEmbeddingJobNeverFetchesAnInternalAddress`
(literal metadata/private/loopback IPs, names resolving inside, a redirect to a private address; the fake handler counts zero requests to the
target), `…AddingAProductToTheWardrobeNeverCopiesAnInternalAddressIntoAPublicImage`, and `OutboundGuardTests` (address table, redirect limit,
mixed answers, the socket handler refusing loopback). Not guarded, on purpose: the JWKS, Open-Meteo, Expo and Anthropic clients (fixed public
hosts in code) and the vision client (internal by design).

### 3.2 Medium

**M1 · Creator click inflation (Fixed).** `/go?post=` appended a `Click` referral and stamped `clicks.creator_id` on every request, and the
creator stats count `clicks.creator_id`. A viewer reloading, a bot, or the creator signed out on another device inflated clicks and the earnings
estimate without limit (only `go`: 120/min per address). *Fix:* `CreatorAttribution.AlreadyEarnedAsync`: one viewer (same user, or signed out
the same salted IP hash) earns a creator once per post and product per 24 h; later clicks still record `post_id` (analytics) but no creator and
no referral row. *Proof:* `SecurityReviewTests.OneViewerEarnsACreatorOnceAPostAndProductADay`. *Left:* a creator clicking their own post signed
out from a different network still earns once a day per network; acceptable while there are no payouts, and payouts must be based on imported
conversions (already the design), never on clicks.

**M2 · Presigned PUT without a size cap (Partly fixed).** `GetPresignedUploadUrl` signs key, verb and content type; S3/MinIO accept any length
up to 5 GB for 15 minutes. `process` refuses > 8 MB, but the object stayed until the daily/hourly cleanup. With free accounts and `uploads`
60/h per user, one account can park 300 GB/h on a single-node MinIO. *Fixed now:* `process` deletes an oversized original at once (wardrobe and
posts; `WardrobeTests`, `SecurityReviewTests.AnOversizedPostUploadIsDeletedWhenProcessingIsRefused`). *Plan:* sign the length: switch the
upload to a presigned **POST** policy with `content-length-range 1..8 MB` (S3, R2 and MinIO all enforce it; the app already sends the size in the
request), and set a MinIO bucket quota in `minio-init` as the backstop. Not done here: it changes the mobile upload contract (multipart form
instead of a raw PUT) and can only be verified against real MinIO. *Decision (CEO, 2026-11-15):* not now; it ships together with the mobile
upload change before the public launch.

**M3 · Wardrobe-tag thumbnails bypass moderation (Fixed).** A photo post was moderated, and so were an outfit post's cutouts, but a wardrobe
tag added at publish showed that item's public thumbnail (`PostResponses`, `image = summary?.ThumbUrl`) and wardrobe photos were never
moderated (`isGarment = false` is still `Ready`). *Exploit:* add an explicit photo to your wardrobe, tag it on a harmless public post: the tag
sheet showed it to everyone. *Fix (CEO decision, 2026-11-15):* `WardrobeModerator` sends the cutout to `/v1/moderate` right after
`wardrobe-analyze` made the item `Ready` (the owner never waits for it) and stores `wardrobe.items.moderation_verdict` and `moderated_at`
(migration `WardrobeModeration`; catalog items are `Allow`, backfilled). Only `Allow` is shareable: for a blocked, borderline or not yet
moderated piece, everyone but its owner gets the tag without `imageUrl`; the owner keeps using the piece everywhere. A call that fails leaves
the verdict empty and `wardrobe-cleanup` asks again daily (`RetryMissingAsync`). The other surfaces never show another user's wardrobe images:
"Dolabımla dene" builds from the viewer's own wardrobe and the post's catalog products, lens matches are the caller's own pieces, profiles show
no wardrobe. *Proof:* `WardrobeModerationTests` (verdicts stored, tag without image for viewers and anonymous on the post, the author's list,
the feed and `try`, an allowed piece shared, the retry, catalog pieces); three of them fail when the gate in `PostResponses` is removed.
*Left:* the cutout and thumbnail objects stay readable by URL (public bucket policy); the URL of an unallowed piece is never handed to anyone
but its owner, and keys are unguessable.

**M4 · Report brigading with throwaway accounts (Fixed).** The third distinct reporter hid a live post, accounts are free and unverified, and
`auth` allows 10 registrations per 5 minutes per address: three accounts silenced any post until a review. *Fix (CEO decision, 2026-11-15):*
every report reaches the review queue (`ReportRules.InReviewQueue` now holds live posts with a report since their last review; the internal
queue, `/admin/review` and the dashboard share it, and `PostReviewer` acts on such posts), but only a trusted reporter's report counts toward
the automatic hide: account at least 72 h old, at least one persona (onboarding confirmed), and once per author per 24 h
(`ReportRules.Counts`, `Reports:*` in configuration, stored per report as `reports.counts_toward_hide`, migration `TrustedReporters`).
*Proof:* `SecurityReviewTests.ThreeThrowawayAccountsCannotHideAPostButTheirReportsReachTheQueue` and
`…ATrustedReporterCountsOncePerAuthorADayAndThreeOfThemHideThePost` (both fail when every report counts again), `SocialRulesTests`.
*Left:* someone patient enough to age three accounts for 72 h and onboard them can still hide a post until a review; the queue sees it at once.

**M5 · "Followers" is not a privacy boundary (Accepted; documented).** `Followers` posts are visible to followers, and following is one tap
with no approval (follow requests are out of scope), so any signed-in account sees them. Blocking removes a follower. Users choosing
"Takipçiler" will expect more. *Decision (CEO, 2026-11-15):* the backend stays as it is and the app rewords the option. The API says so: the
`PUT /posts/{id}/publish` description in the OpenAPI document, `PublishPostRequest` and `PostVisibility.Followers` (`OpenApiTests.PublishingSaysFollowersIsNotPrivate`),
the README API table and the privacy notes. Real restriction needs follow requests, a product decision.

**M6 · Analytics intake as a storage-fill vector (Fixed).** `POST /api/v1/events` is anonymous, takes 50 events of up to 2 KB, and `events`
allows 600 requests per 5 minutes per address: about 17 GB a day per address into `analytics.events`, which had no retention. *Fix (CEO
decision, 2026-11-15):* a daily cap per actor (the user, else the `anonymousId`; `Analytics:DailyEventCapPerActor`, 5,000 per calendar day in
Europe/Istanbul, counted on `received_at`): the excess is rejected as `daily_cap` in the usual 202 and counted by the meter counter
`kombin.analytics.events_capped`. The nightly `analytics-retention` job deletes events received more than `Analytics:RetentionDays` (180) ago;
`weekly_metrics` keeps the aggregates. Indexes on `(user_id, received_at)`, `(anonymous_id, received_at)` and `received_at` (migration
`AnalyticsCapAndRetention`). The privacy notes and the public privacy page state the 180 days (with a lawyer marker, P14). *Proof:*
`AnalyticsLimitsTests` (the two cap tests fail without the cap), `EventCapTests`. *Left:* a script rotating `anonymousId`s is held only by
the `events` rate limit per address (600 per 5 minutes), so one address can still write up to about 17 GB a day; the retention now bounds the total, and a lower anonymous `events` limit is the next step once real traffic is known.

### 3.3 Low

- **L1 · `/go` stored any persona ID (Fixed).** `clicks.persona_id` took the query value as is; when learning from clicks arrives, a forged ID
  would teach someone else's persona. Now kept only when the persona belongs to the signed-in caller.
  `SecurityReviewTests.AClickKeepsOnlyTheCallersOwnPersona`.
- **L2 · Registration reveals existing accounts (Accepted).** `409 Identity.EmailTaken`. Login, `cancel-deletion` and external sign-in do not
  leak (same error and a dummy hash for unknown and passwordless accounts). Hiding it needs e-mail verification (out of scope); `auth` limits
  probing to 10 per 5 minutes per address.
- **L3 · Reuse detection revokes every session of the user (Planned).** Presenting a rotated refresh token revokes all of the user's tokens.
  Whoever holds one old token can sign the user out of every device, again and again (they cannot sign in). *Plan:* revoke only the token's
  family (follow `ReplacedByTokenId` to the head), and age out rotated tokens after the refresh lifetime.
- **L4 · Content created right after `DELETE /me` stays live (Planned).** The deletion request hides existing posts, but an access token stays
  valid up to 15 minutes; a post published in that window is `Live` until the purge. *Plan:* `upload-url`, `from-outfit` and `publish` refuse
  an author whose `DeletionRequestedAt` is set (one indexed lookup).
- **L5 · Unmoderated public text (Planned).** Captions (≤ 300 characters) and `Unknown` tag labels (≤ 60) are shown to everyone without a text filter.
  *Plan:* a word list or a `kombin-vision` text check at publish; reports cover it meanwhile.
- **L6 · One internal key for everything (Planned).** The same `Internal:ApiKey` lets a catalog worker upsert offers and also disable users,
  appoint creators and approve posts. *Plan:* a second key (`Internal:AdminKey`) for identity, social review/creators and notification test
  routes; the admin panel calls the use cases directly and needs neither.
- **L7 · A push token moves to its last registrant (Accepted).** `PUT /me/devices` with another user's Expo token deactivates theirs
  (`Replaced`). That is the phone-switched-accounts case, and Expo tokens are unguessable.
- **L8 · Expensive anonymous reads (Planned).** `GET /feed` (200-candidate ranking SQL) and `GET /catalog/products?q=` (`ILIKE '%term%'` on
  product and brand names) are behind only the global 600/min per address. *Plan:* a `browse` policy (e.g. 120/min per address) and a
  `pg_trgm` index for the search.
- **L9 · Development compose exposure (Accepted, development only).** `docker/compose.yaml` publishes MinIO (`9000`) and the API (`8080`,
  Development: open Hangfire dashboard, OpenAPI) on all interfaces, and MinIO has a default root password. Intended for phones on the LAN and the
  closed-beta tunnel; `compose.prod.yaml` publishes only Caddy and `ProductionSettings` refuses default secrets.

### 3.4 Info (checked, nothing to fix)

- **I1 · Authorization/IDOR:** every id-taking handler filters by the caller; visibility goes through `PostAccess`/`PostRules.CanView`
  (`Private`, `Followers`, blocks both ways, `AccountPending`, `Hidden`, `Blocked`, `NeedsReview`, `Draft`), feed SQL repeats the same
  conditions, tags only show confirmed tags and the author's own wardrobe items (publish refuses another user's item), lens wardrobe matches
  are the caller's, creator stats only the caller's clicks. The harness (§4) proves it route by route.
- **I2 · JWT:** HS256 pinned (`ValidAlgorithms`), issuer, audience, lifetime validated, 30 s skew, `MapInboundClaims = false`; signing key ≥ 32
  bytes, no placeholder outside Development. ID tokens: RS256 pinned, issuer, audience, `exp` required, nonce (raw or SHA-256), JWKS cached with
  a refresh floor. E-mail compared lower-cased on both sides; Unicode look-alikes are different addresses and cannot match a verified one.
- **I3 · Passwords:** ASP.NET Core `PasswordHasher` (PBKDF2-HMAC-SHA512, 100k iterations, rehash on login), 8–128 with a letter and a digit.
- **I4 · Access-token window:** disable, deletion and logout end refresh tokens, not access tokens (≤ 15 min); documented in CLAUDE.md.
- **I5 · Internal surface:** constant-time key compare, empty key refused, key required at start-up (≥ 32 chars, no placeholder outside
  Development); every internal route sits on a `RequireInternalKey` group (tested). Caddy and the app agree; the app check stands on its own.
- **I6 · Admin:** separate cookie scheme, `HttpOnly`, `SameSite=Strict`, `Secure` outside Development; sign-in issues a fresh cookie (no
  fixation); antiforgery on every POST; `LocalRedirect` with a checked `returnUrl`; throttle keyed on the forwarded address, which is believed
  only from `Proxy:KnownNetworks` with `ForwardLimit = 1` (a client's own `X-Forwarded-For` is ignored); every action audited. Razor encodes all
  output and there is no `Html.Raw`, so captions, handles, report reasons and seller URLs cannot inject markup; `href` values are http(s) by
  validation. The CSP still allows `'unsafe-inline'` scripts (one inline confirm script): a nonce would harden it further.
- **I7 · Injection:** raw SQL is interpolated (`SqlQuery`/`ExecuteSqlInterpolated`, parameterised), `FeedRanking.BaseSql` and `VisualIndex`'s
  `SET LOCAL` are constants, metric SQL binds Npgsql parameters, `ILIKE` patterns are escaped, the admin search uses EF `Contains`. No file path
  comes from a client. Logs are structured (no string-built log lines); `/go` redirects only to a stored http(s) URL (no parameter changes the
  destination; a template that does not expand to http(s) falls back).
- **I8 · Mass assignment:** request records carry only user-settable fields; status, counts, ownership, moderation and creator flags are never
  bound.
- **I9 · Uploads:** server-built keys, content type signed, metadata stripped before anything reads a post photo (`ImageMetadata.Strip`,
  unparseable refused) and renditions re-encoded from pixels by `kombin-vision`; wardrobe originals stay private and only re-encoded cutouts
  and thumbnails are public. Pixel-count limits (image bombs) must live in `kombin-vision`, which decodes; the API parses segments without
  decoding.
- **I10 · Secrets and errors:** only `dev-only-…-change-me` placeholders in `appsettings.Development.json`, refused in Production;
  `GlobalExceptionHandler` hides exception detail outside Development; `ProductionSettings` lists every problem without printing a value. CORS is
  deliberately absent (mobile only). `/admin` has its own security headers; the JSON API sends none beyond HSTS behind TLS, which is fine for a
  non-browser client.
- **I11 · Dependencies:** `dotnet list package --vulnerable --include-transitive`: nothing flagged in any project (2026-11-14). CI now fails on
  any finding (`Vulnerable packages` step).
- **I13 · Budget outfits and trips (Task P1, 2026-11-15):** the anonymous budget route reads only the catalog and writes nothing, and is rate
  limited per address like visual search (`budget`); the signed-in routes take a persona (the catalog convention: someone else's is 404) and
  trip IDs filtered by the caller (`GetTrip`, `DeleteTrip`, `ReplanTrip`: 404 for another user's, the harness proves it). A saved trip stores
  the server's own plan, never a client-supplied snapshot, so nobody can store arbitrary JSON; at most 50 per user. The trip city goes to
  Open-Meteo's fixed hosts (no `AddPublicOnlyGuard` needed: the host is ours to choose, the city is only a query parameter, escaped). Candidate
  SQL is interpolated (parameterised). Buy links are `/go` with `src=budget` / `src=trip`; no response carries `Offer.Url`.
- **I12 · Concurrency:** like and follow counts are recomputed from rows (`ON CONFLICT DO NOTHING`), refresh rotation has an `xmin` concurrency
  token, alert notifications are deduplicated for 24 h, external first sign-ins retry once on the unique indexes, publish refuses a post still
  `Processing`, the purge checks the deletion window. Two simultaneous third reports can miss the hide (each counts two); a later report or the
  queue catches it.

## 4. The authorization harness

`tests/Kombin.Api.IntegrationTests/AuthorizationMatrixTests.cs`:

- `Cases` maps every authenticated route (`"METHOD /pattern"`) to a **Probe** (user B sends user A's identifier; expected status, and A's data
  checked unchanged in the database), a **ScopedRead** (no identifier; B's answer must contain none of A's identifiers: user id, e-mail, persona,
  item, alert, size, outfit, device token, post ids) or an **Exempt** with the reason.
- `EveryAuthenticatedRouteHasACrossUserCase` reads the running app's `EndpointDataSource` and fails on a route without a case or a case without a
  route. **Adding an authenticated endpoint means adding its case.**
- `OptionalAuthRoutesRefuseWhatTheCallerMayNotSee` and `ListsNeverShowAnotherUsersHiddenPosts` cover the signed-in-or-out routes (private,
  followers-only and draft posts; someone else's persona on catalog, feed and profile lists).
- `EveryInternalRouteRefusesACallWithoutTheKey` and `EveryAdminPageButTheLoginNeedsTheAdminSignIn` enumerate those surfaces the same way.
- Sanity check done during the review: removing the owner filter from `GET /me/wardrobe/items/{id}` makes the harness fail on exactly that route.
