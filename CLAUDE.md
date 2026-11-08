# Kombin backend — guide for agents

## Product

Kombin is a mobile app that learns a user's style(s), manages their wardrobe, suggests outfits, compares prices across
Turkish retailers (Trendyol, Hepsiburada, brand sites) and redirects users to buy through affiliate links. The app never
sells anything itself. This repository is its backend: a .NET 10 Minimal API on PostgreSQL 18.

## Architecture rules (non-negotiable)

- **Minimal API only.** No controllers, no MediatR, no AutoMapper, no generic repository.
- **Vertical slice architecture.** One folder per feature, one folder per use case inside it. No
  Application/Domain/Infrastructure layers.
- **Handlers use `KombinDbContext` directly.** There is no repository or service layer in between.
- **Entities are shared across slices**, so they live in `src/Kombin.Api/Persistence/Entities/<Schema>/`, never inside a slice.
  Their EF configuration lives in `Persistence/Configurations/<Schema>/`.
- **One PostgreSQL schema per feature** (`identity`, `catalog`, `engagement`, `profile`, `recommendation`, ...). Tables and columns are snake_case (naming convention
  is global). Migrations live in `Persistence/Migrations`.
- Types are `internal` by default; the test projects see them through `InternalsVisibleTo`. Only `Program` is public.
- C# latest, nullable on, implicit usings, file-scoped namespaces. `TreatWarningsAsErrors=true`,
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`: fix warnings, never suppress them.
- A NuGet package is added only if it is strictly required (central versions in `Directory.Packages.props`).
- All code, comments, docs and commit messages are in English.

## Folder convention

A use case is one `Endpoint.cs` (route, request/response records and handler in the same file) plus an optional
`Validator.cs`:

```
src/Kombin.Api/Features/Identity/Register/Endpoint.cs
src/Kombin.Api/Features/Identity/Register/Validator.cs
src/Kombin.Api/Features/Identity/IdentityModule.cs      # MapIdentityEndpoints(): groups, routes, feature DI
src/Kombin.Api/Persistence/Entities/Identity/User.cs
src/Kombin.Api/Persistence/Configurations/Identity/UserConfiguration.cs
```

Cross-cutting code lives in `src/Kombin.Api/Common/` (Results, Http, Auth, Validation, Pagination, Time, Health, Storage). Development
seed data lives in `src/Kombin.Api/Persistence/Seed/`.

Internal endpoints (called by workers, not the app) live under `/api/v1/internal/...` and use `.RequireInternalKey()`. The ones that
exist: `POST /internal/catalog/offers/upsert`, `POST /internal/catalog/offers/sweep`, `GET /internal/catalog/sellers/{sellerSlug}/offers`,
`PUT /internal/catalog/sellers/{slug}` (see Catalog ingestion rules), `POST /internal/notifications/test`,
`GET /internal/analytics/weekly` (see Analytics rules), and `PUT /internal/social/creators/{userId}`, `GET /internal/social/review-queue`
and `POST /internal/social/posts/{id}/review` (see Social rules).

## How to add a new use case

1. Create `Features/<Feature>/<UseCase>/Endpoint.cs` with the request/response records, a `static class <UseCase>Endpoint`
   exposing `Map(RouteGroupBuilder group)`, and a private `Handle` method that takes the request, `KombinDbContext` and
   whatever else it needs from DI.
2. If the request has rules, add `Validator.cs` (an `AbstractValidator<TRequest>`) and chain `.WithValidation<TRequest>()`
   on the route. Validators are found by assembly scanning; do not register them by hand.
3. Call `<UseCase>Endpoint.Map(group)` from `Features/<Feature>/<Feature>Module.cs`. Add `.RequireAuthorization()` on the
   route or group unless the endpoint is public. Get the caller from `ICurrentUser`.
4. If it needs a new table: add the entity under `Persistence/Entities/<Schema>/`, a configuration under
   `Persistence/Configurations/<Schema>/`, a `DbSet` on `KombinDbContext`, then create a migration (commands below).
5. Add tests: validator and logic in `tests/Kombin.Api.Tests`, the HTTP flow in `tests/Kombin.Api.IntegrationTests`.

## Result, Error and validation conventions

- Expected failures are values: return `Error` / `Result` / `Result<T>` (`Common/Results`). Exceptions are for bugs and
  are turned into a generic 500 by `GlobalExceptionHandler`.
- Define a feature's errors once, e.g. `IdentityErrors`: `Error.Conflict("Identity.EmailTaken", "...")`. Codes are
  `<Feature>.<Name>` and are part of the API contract (clients read `errorCode`).
- `error.ToHttpResult()` / `result.ToHttpResult()` map `ErrorType` to 400/401/403/404/409/503/500 as `application/problem+json`
  with an `errorCode` extension and the `correlationId`.
- Input validation uses FluentValidation through `ValidationFilter`: failures become a 400 problem with an `errors`
  dictionary (`{ "email": ["..."] }`, camelCase keys). Do not validate inside handlers what a validator can express.
- Every response, including errors, carries `X-Correlation-ID`.

## Identity rules

- **Three ways in, one token pipeline.** `POST /auth/register`, `POST /auth/login` and `POST /auth/external` (Google or Apple) all end in
  `AuthTokenIssuer.Issue` and return the same `AuthTokensResponse { accessToken, refreshToken, expiresIn, isNewUser }`; refresh-token
  rotation is shared too. `isNewUser` is true only when the request created the account (register, or a first external sign-in); login
  and refresh say false. Never mint tokens anywhere else.
- **External sign-in takes an ID token, nothing else.** The app gets an OpenID Connect ID token from Google or Apple on the device and posts
  `{ provider, idToken, displayName?, deviceName?, anonymousId?, nonce? }`. No OAuth redirect flow, no server-side login page, no provider
  access or refresh token is ever stored. `IExternalTokenVerifier` (`Features/Identity/ExternalTokens`) dispatches by provider to
  `GoogleTokenVerifier` / `AppleTokenVerifier`, which share `IdTokenValidation` (RS256 against the provider's JWKS, issuer, audience in the
  configured client IDs, `exp`/`nbf` against `IClock` with 2 minutes of skew, a `sub`, and the nonce when sent: the raw value or its
  SHA-256 hex). `JwksKeySource` caches each provider's keys for 24 h, refreshes once on an unknown `kid` (at most once a minute), and keeps
  the old keys if a refresh fails. Built on `Microsoft.IdentityModel.JsonWebTokens` (already there through JwtBearer); no Google or Apple SDK.
- **Identity comes from the token's claims only.** `sub`, `email`, `email_verified` and `name` are read from the verified token; nothing in
  the request body (`displayName` included) is ever used to decide who the caller is. `displayName` is applied only when an account is
  created (Apple gives the app the name once).
- **Find-or-create** (`ExternalSignInRules`, pure): (provider, subject) linked -> that user; else the token needs a verified email
  (`Identity.ExternalEmailMissing` otherwise) -> an account with that email gets linked (safe only because both providers verify the email:
  Google's `email_verified` must be true, Apple's counts as true unless it says `false`, as a boolean or the string); else a new account with
  `password_hash = null` and display name request -> token `name` -> email local part (cut to 50). Private-relay Apple emails are ordinary
  emails. Two simultaneous first sign-ins rely on the unique indexes (`identity.external_logins (provider, subject)`, `users.email`): the
  loser clears its change tracker and retries the lookup once. After the commit it calls `IAnalyticsIdentityLinker.LinkAsync`, like register
  and login.
- **Passwords are optional.** `users.password_hash` is nullable. A passwordless (Google/Apple-only) account answers `POST /auth/login` with
  the same `Identity.InvalidCredentials` as an unknown email, after the same dummy hash check; never reveal how an account signs in.
- **Errors:** `Identity.ExternalTokenInvalid` (401, every token defect, deliberately one code), `Identity.ExternalEmailMissing` (400),
  `Identity.ExternalProviderDisabled` (503, no client ID configured for that provider), `Identity.ExternalProviderUnavailable` (503, the
  provider's keys could never be fetched).
- **Config:** `Authentication:Google:ClientIds` (Android, iOS and web client IDs) and `Authentication:Apple:ClientIds` (the iOS bundle ID,
  later the Services ID), env `Authentication__Google__ClientIds__0`, ...; compose maps `GOOGLE_CLIENT_ID_ANDROID/_IOS/_WEB`,
  `APPLE_CLIENT_ID_IOS` and `APPLE_SERVICES_ID`. Blank entries are ignored. Never commit real IDs. Integration tests replace the verifier with
  `FakeExternalTokenVerifier` (the "token" spells out `sub=...;email=...;verified=...;name=...`); the real verifiers are unit-tested with
  tokens signed by test RSA keys against a fake JWKS handler.

## Catalog ingestion rules

External Python workers (a separate repo) push the catalog through the internal endpoints below, all behind `X-Internal-Key`.

- **`POST /internal/catalog/offers/upsert`** (`OfferUpserter`, up to 500 items). The response is
  `{ created, updated, snapshotsWritten, results: [{ index, externalId, productId, offerId, created }], errors }`: `results` holds the
  stored items, `errors` the rejected ones, and together they account for every index. `product` may carry `brandWebsiteUrl` /
  `brandLogoUrl`: they are set when the brand is created and filled in later only while the brand has none, never overwritten (this
  also happens on an existing offer). `imageUrls` holds at most 6 entries. Image, brand website and seller website URLs must be
  absolute http(s) with a host, no credentials and no whitespace (`UpsertOfferItemValidator.IsPublicHttpUrl`).
- **`PUT /internal/catalog/sellers/{slug}`** with `{ name, kind, websiteUrl, affiliateNetwork?, affiliateUrlTemplate?, affiliateParams?,
  isActive }` registers or updates a seller before the first upsert. `name`, `kind`, `websiteUrl` and `isActive` (default
  true) are replaced on every call; the affiliate fields are not: **left out = unchanged, explicit null = cleared**, so a worker that only
  knows the shop never wipes hand-configured affiliate settings. Sellers also have `country` (`char(2)`, default `TR`), returned on the PUT
  response and on the `seller` of product offers and price history; it is not settable yet and nothing filters on it. `kind` is `Marketplace` or `BrandStore`, the slug is kebab-case, and a template must contain `{url}` or
  `{rawUrl}`. A name another seller uses is 409 `Catalog.SellerNameTaken`. The upsert's `<brand>-store` auto-create stays for backward
  compatibility (a PUT may take such a seller over).
- **`GET /internal/catalog/sellers/{sellerSlug}/offers?updatedAfter=&page=&pageSize=`** pages through every offer of a seller, inactive
  ones included, ordered by `externalId` so paging stays stable while workers upsert (`pageSize` default 100, clamped to 1000). Items are
  `{ externalId, productId, modelCode, currentPrice, stockStatus, lastSeenAt, isActive }`. `updatedAfter` filters on the offer's
  `UpdatedAt`, which moves on every upsert and when the sweep deactivates the offer, so a delta run sees re-priced and vanished listings.
- **`POST /internal/catalog/offers/sweep`** with an optional body `{ sellerSlug }` runs `OfferSweepJob` on demand and returns
  `{ offersDeactivated, productsDeactivated, productsReactivated }`; an unknown seller is 404.
- **Offer sweep** (`Features/Catalog/Jobs/OfferSweepJob.cs`, rules in the pure `OfferSweepRules`). It follows the Notification jobs'
  conventions (plain class, constructor injection, idempotent, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`,
  tests call `SweepAsync(sellerId, ct)` directly, `Hangfire:Enabled` gate) and is registered by `UseCatalogJobs` (called in `Program.cs`
  right after `UseNotificationJobs`).

  | Job id | Schedule | Does |
  |---|---|---|
  | `offer-sweep` | daily 04:30 `Europe/Istanbul` | active offers with `LastSeenAt` more than 48 h old (exactly 48 h is still fresh) → `IsActive = false`, `StockStatus = OutOfStock`, plus a closing `PriceSnapshot` (same price, out of stock); then `Product.IsActive` repaired in both directions to "has an active offer". Batches of 500, logs the counts |

  Each deactivated offer goes through `AlertEvaluator.EvaluateAsync` with `EvaluationReason.Sweep`, which skips every rule: **a sweep
  never fires an alert, `AnyChange` included** (`OfferUpserter` passes `Observation`). The evaluator keeps no state; its "previous" state
  is the offer's stored stock and price, and the sweep leaves the offer out of stock at the same price, so the next upsert that finds
  it in stock is a restock to `BackInStock` and `PriceDrop` (and a change to `AnyChange`). Always pass a seller filter in tests: the
  unfiltered sweep retires every stale offer in the shared database.
- **`GET /catalog/products` items carry `cheapestSeller { name, slug }`**: the seller of the offer that defines `lowestPrice` (the lowest
  sellable offer, smallest offer ID among equals; the same ordering as `lowestOriginalPrice`). `GET /me/saved` items repeat it (a product
  nobody sells has `null`), because saved items must contain every catalog list item field. The OpenAPI document is generated from the
  records, so there is no schema file to edit.

## Engagement rules

- **Buy links are always `/api/v1/go/{offerId}`, never a raw seller URL.** Every outbound click must go through that
  redirect: it records a `Click` and applies the seller's affiliate template. No response may contain `Offer.Url`:
  `GET /catalog/products/{id}` offers carry `goPath` (`"/api/v1/go/{offerId}"`) and no `url`, and clients open the
  `goPath`. `Offer.Url` stays in the database for the redirect and the ingestion workers only. Social post tags follow the same rule
  (`goPath = /api/v1/go/{offerId}?src=feed&post={postId}`); `post=` is how `/go` attributes a click to a post and its creator. `/go` is the one anonymous route that still reads the user: use `ICurrentUser.UserIdOrNull`
  on endpoints that work signed in or out (`UserId` throws).
- **Alert evaluation lives in the catalog upsert path.** `OfferUpserter` calls `Features/Engagement/AlertEvaluator`
  after each group commits, for every existing offer whose effective price or stock status changed. Alerts are never
  evaluated on user requests or in a separate job. The rule logic is the pure `AlertEvaluator.Evaluate`; evaluation
  failures are logged with the offer ID and must never fail the upsert. Anything that changes offers must go through
  `OfferUpserter` (or the offer sweep, which evaluates the offers it deactivates) or alerts will not fire.
- Trigger rows (`engagement.alert_triggers`) are the source of the notifications: the Notification feature's `alert-dispatcher` job
  reads the unconsumed ones and sets `ConsumedAt`. Clicks and triggers are append-only (`ConsumedAt` is the one field that is updated).
- `Outbound:IpSalt` (env `Outbound__IpSalt`) salts the stored IP hash; real affiliate templates are per-seller data
  (`AffiliateUrlTemplate`, `AffiliateParams`), not code.

## Profile rules

- A user has several **personas** (Ofis, Gece, ...; at most 6 non-archived), each with its own explicit preferences and its
  own learned `PersonaStyleProfile`. Everything style-related hangs off a persona, never off the user. `UserProfile`
  (audience, measurements, active persona, onboarding flag) is created lazily by the first `GET /me/profile`.
- **`personaId` convention on catalog endpoints.** `GET /catalog/products` and `GET /catalog/products/{id}` accept an
  optional `personaId`. It must belong to the signed-in caller and not be archived: anonymous + `personaId` is 400
  `Profile.PersonaRequiresAuth`, someone else's or an archived persona is 404 `Profile.PersonaNotFound` (never reveal that
  it exists). With it, items carry `matchScore` (0-100); without it the field is absent. `sort=match` needs a `personaId`.
  New endpoints that list products for a persona should follow the same convention instead of inventing another.
- **Scoring is in memory.** `MatchScorer.Score` (`Features/Profile`, formula documented at the top of the file) is pure and
  rule-based; it is applied to the page's items after paging. `sort=match` is the exception: it scores the newest 500
  products that pass the filters and pages those (`ListProductsEndpoint.MatchCandidateLimit`, a TODO for the
  Recommendation task). Do not push the formula into SQL, and do not change a weight without updating the comment block.
- `PersonaStyleProfile` is derived data (`StyleProfileBuilder`, pure): recomputed after every swipe batch and whenever a
  persona's preferred/avoided colors or preferred fits change; never written by an endpoint directly. A persona always has
  a row (created with it); readers fall back to deriving one rather than failing.
- **`PUT /me/profile` follows the seller PUT:** left out = unchanged, explicit null = cleared (the `*Set` flags on `UpdateProfileRequest`).
  `Audience` is the exception, a null keeps it too. `City` is part of the mobile contract: the outfit engine reads the weather from it.
- **Style cards are content, seeded in every environment; user data never is.** `Persistence/Seed/style-cards.json`
  (embedded) is applied at every startup by `StyleCardSeeder` (idempotent, matched by the fixed card `Id`, removed cards are
  switched off, not deleted, so swipes stay valid). Never change a card's `id`. If migrations are pending the seeder skips
  with a warning (outside Development they are applied by hand). The dev seed (`dev-catalog.json`, demo user) stays
  Development-only, and no seed may create personas, swipes, sizes or profiles. Because the cards exist in every database,
  Profile tests may rely on `style-cards.json` (unlike the dev catalog, which tests never touch).
- Sizes: `SizeEntry` is unique per (user, brand, group); the group comes from the top-level category slug
  (`SizeGroups`). A hint's inferred `suggestion` (needs 2 agreeing entries at other brands) is computed per request and
  never stored; nothing may persist a size with source `Inferred` without a decision to do so.

## Notification rules

- **What exists:** devices (Expo push tokens), per-user preferences, the `notification.notifications` table (it is both the push to
  deliver and the in-app feed), four Hangfire jobs, and the push delivery through the **Expo Push Service**.
- **Expo is the only push path.** The app registers Expo push tokens; the backend never talks to FCM or APNs. The client is a plain
  `HttpClient` behind `IExpoPushClient` (`Features/Notification/ExpoPushClient.cs`); it never throws on a non-2xx, it returns a
  result the job interprets, and tests replace it with a fake. `Expo:AccessToken` (env `Expo__AccessToken`) is optional and only
  sent as a bearer when set. No other push or messaging package, no email, no SMS.
- **Jobs live in `Features/Notification/Jobs/`, are plain classes with constructor injection, are idempotent, and run from Hangfire**
  (PostgreSQL storage, schema `hangfire`, same database). **Tests call them directly**; the Hangfire server is never started in tests
  (`Hangfire:Enabled=false`). The core of every job takes an optional user filter so integration tests that share one database only
  touch their own rows; `weekly-matches` also takes the products to consider, because its candidates are global: its tests pass the ones they created. Every job has `[DisableConcurrentExecution(60)]` and `[AutomaticRetry(Attempts = 0)]` (the next run is the retry).

  | Job id | Schedule | Does |
  |---|---|---|
  | `alert-dispatcher` | every minute | up to 500 unconsumed `AlertTrigger`s → `Notification` rows; consumes each trigger in the same transaction |
  | `push-sender` | every minute | due `Pending` notifications → Expo, batches of 100 messages, one message per active device, one row per notification |
  | `push-receipts` | every 15 min | Expo receipts for `Sent` pushes 15 min - 24 h old, once each (`ReceiptCheckedAt`) |
  | `weekly-matches` | Sunday 19:00 `Europe/Istanbul` | per user with a device: top 3 new products (score ≥ 70) for the active persona → one `NewMatches` notification |

- **Notifications are created by the jobs, never by request handlers** (the one exception is the internal QA endpoint
  `POST /internal/notifications/test`). Rules of the dispatcher, in order (`DispatchRules`, pure): preference for the kind off
  (`PriceDrop`/`AnyChange` → `PriceAlerts`, `BackInStock` → `StockAlerts`) → no row, trigger still consumed; same
  `DedupeKey` for the user within 24 h (`pricedrop:{productId}`; `DispatchRules.DedupeWindow`) → no row, consumed; no active device →
  `Suppressed` row with `FailureReason = NoDevice` (the feed shows it); otherwise `Pending`, scheduled now or at the end of quiet hours.
- **Statuses:** `Pending` → `Sent` | `Failed` | `Suppressed`. The feed and unread count show everything except `Failed`. A device the
  push service reports as `DeviceNotRegistered` is deactivated (`DeactivationReason`), and a notification that reached no device
  because of that is `Suppressed` (`DeviceNotRegistered`), not `Failed`, so the user still sees it in the app. Any other ticket or
  receipt error with nothing delivered is `Failed` with the reason. A failed HTTP call leaves the row `Pending` and counts an attempt;
  the 5th failed attempt makes it `Failed`.
- **Devices:** a token is active for one user at a time (partial unique index on active tokens). Registering it under another user
  deactivates the old row (`Replaced`) and adds/reactivates the new one; logout (`expoPushToken` in the body) and
  `DELETE /me/devices/{token}` deactivate with `UserRemoved`. Other features reach this only through `IDeviceDeactivator`.
- **Time zone:** quiet hours (`HH:mm`, both or neither, may wrap midnight) and the weekly schedule are in `Europe/Istanbul`
  (`QuietHours.Zone`). The Docker runtime image installs `tzdata` for it. Stored instants are always UTC (`timestamptz`).
- **Hangfire wiring:** `AddNotificationFeature` registers storage lazily; `UseNotificationJobs` (called in `Program.cs` after the EF
  migrations and the seeds) is what first creates the `hangfire` schema, registers the recurring jobs (`UseCatalogJobs` adds the
  Catalog's `offer-sweep`, `UseAnalyticsJobs` the Analytics' `weekly-metrics`, `UseWardrobeJobs` the Wardrobe's `wardrobe-cleanup` and
  `UseSocialJobs` the Social's `social-purge` right after it, behind the same flag; `outfit-narrate` and `post-process` are fire-and-forget,
  not recurring) and, **in Development only**,
  maps the dashboard at `/hangfire` (open, no auth, unmapped in every other environment). Nothing happens unless `Hangfire:Enabled` is
  true (compose sets it; tests set it to false; `Development` defaults to true). The storage uses `EnableLongPolling`
  (LISTEN/NOTIFY), so an enqueued fire-and-forget job (`wardrobe-analyze`) starts at once instead of on the next 15-second queue poll.
- **Push payload:** the message `data` is the stored `Notification.Data` plus `notificationId` and `kind` (`NotificationData.ForPush`), so
  the app reports `notification_open` for exactly that row. Keep both keys when changing the payload.
- **Copy is Turkish and lives in `Features/Notification/Copy.cs`** (money as `2.149 ₺` / `849,90 ₺`, formatted by hand because the runtime
  image may run with invariant globalization). Clients read `Notification.Data.deepLink`; do not change its shape without the app team.

## Analytics rules

- **What exists:** `POST /api/v1/events` (public), the `analytics.events` table, `IAnalyticsIdentityLinker`, the `weekly-metrics` job with
  `analytics.weekly_metrics` and `GET /internal/analytics/weekly`, `docs/metrics.sql`, and an optional Metabase in compose.
- **The allow-list is the privacy guarantee.** `AnalyticsEventCatalog` (pure) lists every event name and, per event, its properties with
  their types; the validator rejects anything else. A new event or property is a catalog entry plus a README row, nothing more. Property
  values are GUIDs, integers, booleans, enum names or short identifiers, never free text (`search` sends `queryLength`, never the query). Keys named
  like personal data (`email`, `name`, `phone`, `address`, `token`) are refused on every event, and so is any key not listed for the event.
- **A bad event costs only itself.** `AnalyticsEventCatalog.Reject` judges each event; the handler stores the rest and lists the others in
  `rejected` of the 202 as `{ index, reason }` (`RejectionReasons`): `unknown_event` (name outside the allow-list), `invalid_properties`
  (unlisted, missing, wrongly typed or nested property, a PII-named key, over 2 KB), `missing_identity` (anonymous caller without
  `anonymousId`) and `duplicate` (same `sessionId`, `name` and client `occurredAt` earlier in the batch; across batches nothing is
  deduplicated). A rejected event does not use up a duplicate slot. 400 `Validation.Failed` is only for a malformed body (the validator
  checks the envelope: 1-50 events, no null event, `sessionId`, `platform`, `appVersion`, `occurredAt`, `anonymousId`); never move the
  name or property rules back into the request validator.
- **Clock skew is clamped, not rejected** (`EventTiming`): `occurredAt` more than 7 days old or 5 minutes ahead becomes `ReceivedAt`.
  `OccurredAt`/`ReceivedAt` are always UTC.
- **Identity is linked after the commit and never breaks a sign-in.** Login and Register take an optional `anonymousId` and call
  `IAnalyticsIdentityLinker.LinkAsync` (the only coupling between Identity and Analytics), which runs
  `UPDATE analytics.events SET user_id = @user WHERE anonymous_id = @anon AND user_id IS NULL`, logs and swallows failures, and never takes
  over events that already have a user. Events keep their `anonymous_id` after login.
- **Metrics have one source of truth: `docs/metrics.sql`.** It is embedded in the assembly (`MetricSql`; the Dockerfile copies it into the
  build) and each `-- name:` block is run with `:cohort_start`, `:cohort_end` and `:as_of` bound as Npgsql parameters, so what operators run
  by hand is what the job runs. An actor is `COALESCE(user_id::text, anonymous_id)`; a cohort is the actors whose first `app_open` falls in
  `[cohort_start, cohort_end)`. Each block returns `(numerator, denominator)`. Change a metric there, and update the README table.
- **`WeeklyMetricsJob`** (plain class like the other jobs, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`, tests call
  `ComputeAsync(asOf, ct)`, registered by `UseAnalyticsJobs` behind `Hangfire:Enabled`, called in `Program.cs` after `UseCatalogJobs`) runs
  Monday 08:00 `Europe/Istanbul` (`weekly-metrics`) for the last 4 cohort weeks (Monday-based in Istanbul, `CohortWeeks`) and overwrites
  the rows of `(week_start, metric)`. D7 retention of last week's cohort cannot exist yet, which is why it revisits earlier weeks; its
  denominator counts only members whose day 8 is over. A metric with an empty cohort writes no row. Values are percentages (0-100).
- **Tests for metrics seed a cohort in 2020** by inserting events directly (the API clamps old clocks), so no other test's data falls
  into those weeks; never seed a cohort in recent weeks, the database is shared.
- **Metabase** is a compose service behind the `metabase` profile (not part of `up` or CI's smoke test), loopback-only, with its own H2
  file in a named volume. It connects as `metabase_ro` (`docker/postgres/metabase_ro.sql`, mounted into `docker-entrypoint-initdb.d`, so
  a pre-existing volume needs the file run by hand once): read-only, and limited to the `analytics`, `engagement` and `catalog` schemas.

## Wardrobe rules

- **The rule: a garment from a photo in ~3 seconds, with at most one tap to confirm.** The server does the work (background removal,
  category, colors, pattern); the user only corrects. Every change to this feature is judged against it: no extra required field, no
  extra round trip on the photo path, nothing that makes the user wait for something the server could guess. `PATCH {}` is the one tap.
- **Flow:** `POST /me/wardrobe/items/upload-url` creates a `Processing` item and a presigned PUT for its original → the app uploads
  straight to storage → `POST /items/{id}/process` checks the object (missing 409, over 8 MB 400) and enqueues `wardrobe-analyze` →
  the app polls `GET /items/{id}`. Items created but never processed are hidden from lists and stats (`VisibleOf`) and deleted after a day.
- **Vision service contract** (`kombin-vision`, a separate repo; its `README.md` is the source of truth). `POST /v1/garment/analyze`,
  multipart field `image`, header `X-Internal-Key` = `Vision:InternalKey` (env `Vision__InternalKey`, compose `VISION_INTERNAL_KEY`; not our
  own `Internal:ApiKey`). Answer: base64 `cutout` (PNG) and `thumbnail` (WEBP), `category` + `categoryCandidates` (top 3, slugs of our
  leaf categories), `primaryColor`/`secondaryColor` (`ProductColor` names + hex), `pattern` (`ProductPattern` name), a 512-float unit
  `embedding`, `quality { isGarment, confidence, warnings }`. Errors are `{ "detail": "<code>" }`. The typed client is `IVisionClient`
  (`Features/Wardrobe/VisionClient.cs`, 30 s timeout); like the Expo client it never throws for a failed call: 4xx (except 401/408/429)
  is permanent, the rest transient. It also reads `/health`'s `clipModel` once per process for `EmbeddingModel`. Social posts use two more
  endpoints of the same service through the same client: `POST /v1/moderate?renditions=true` and `POST /v1/detect` (see Social rules).
- **`wardrobe-analyze`** (`Jobs/WardrobeAnalyzeJob.cs`): fire-and-forget per item, `[AutomaticRetry(Attempts = 3)]` with 5/15/30 s delays.
  It only works on a `Processing` item and writes the same keys and fields on every run (idempotent). Category = top candidate when its
  confidence is ≥ 0.35 (`WardrobeAnalysis.CategoryThreshold`), else null and `needsCategory`. `isGarment=false` is still `Ready`, with the
  `not_garment` warning. A permanent error fails the item at once; transient errors are counted on the item (`AnalysisAttempts`) and the
  4th makes it `Failed` (the count is ours, not Hangfire's, so tests drive it by calling `AnalyzeAsync`). `AnalysisJson` is the answer
  without images and embedding. A failed item can be filled in by hand (any PATCH makes it `Ready`) or processed again.
- **`wardrobe-cleanup`** (daily 05:00 `Europe/Istanbul`, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`, tests call
  `CleanupAsync(userId, ct)`): deletes the objects of items archived more than 30 days ago (rows and wear history stay, keys set to null)
  and the abandoned uploads above.
- **From the catalog:** `POST /items/from-product` makes a `Ready`, already confirmed item (`FromCatalog`) and copies the product's primary
  image into storage as both cutout and thumbnail, unchanged (no background removal, no resizing: there is no image library here). A failed
  copy still creates the item, which then shows the catalog image. The same product twice returns the existing item.
- **Wears** are a set of days per item (unique `(item_id, worn_on)`, `wornOn` defaults to today in `Europe/Istanbul`); `WearCount` and
  `LastWornAt` are recomputed from the rows after every change (`WearRules.Summarize`), never incremented. `OutfitId` (no foreign key) is
  set when an outfit is marked worn. Recording a day goes through `WearSummaries.RecordAsync`, shared with the outfit engine's `worn`.
  `GET /stats.possibleOutfits`, `GET /stats.unmatchedCount` (pieces whose `companionCount` is 0) and `GET /gaps.unlocksEstimate` come
  from the outfit engine (see Recommendation rules).
- **Pairings:** `GET /items/{id}/pairings` and `companionCount` on `GET /items` (only there: the record is shared, the field is absent elsewhere)
  come from the engine too (see Recommendation rules). The app leads with them; wear tracking stays in the data but out of the foreground.
- **Gap analysis** is the pure `GapAnalysis.Analyze` (rules at the top of the file, unit-tested with synthetic wardrobes). Change a rule there
  and in that comment block together. Suggested products for the first gap follow the catalog persona convention (match score ≥ 60 with a
  persona; `GetGapsEndpoint.MinMatchScore`).
- **Storage** (`Common/Storage`, shared by later features): `IObjectStorage` (`PutAsync`, `GetAsync`, `GetSizeAsync`, `GetPresignedUploadUrl`,
  `GetPresignedDownloadUrl`, `GetPublicUrl`, `DeleteAsync`) over AWSSDK.S3, so MinIO, S3 and R2 all work. Keys are
  `{area}/{userId}/{ownerId}/{original|cutout|thumb}.{ext}` (`StorageKeys.Build`). **Visibility is decided by the file name**: the bucket
  policy (`docker/minio/public-read.json`, applied by the `minio-init` container) makes `*/cutout.*` and `*/thumb.*` anonymously readable;
  everything else, originals included, is private and served through a presigned GET (social posts' `feed.*` and `preview.*` too: a
  followers-only photo must not be readable by URL). Never store anything private under those names.
  Settings `Storage:Endpoint/PresignEndpoint/Bucket/AccessKey/SecretKey/PublicBaseUrl/ForcePathStyle`, validated when the store is first
  used (not at start-up). `PresignEndpoint` exists because a presigned URL is signed for its host: compose's API talks to `minio:9000`
  while the app uploads to `STORAGE_PUBLIC_URL`.
- **pgvector** is installed (`CREATE EXTENSION vector` in the `Wardrobe` migration via `HasPostgresExtension`, `UseVector()` on the Npgsql
  options, image `pgvector/pgvector:pg18` in compose and in the test containers). `WardrobeItem.Embedding` (`vector(512)`) is the only vector
  column so far; nothing queries it yet. A test that builds its own `DbContextOptions` must call `UseVector()` too.
- **Tests** replace the outside world in `ApiFactory`: `FakeVisionClient` (answers by the photo's text: `unreachable`, `refused`, `unsure`,
  `not-garment`), `FakeObjectStorage` (in memory; the S3 implementation is exercised against MinIO in the compose walkthrough, not in the
  test suite), `RecordingJobClient` for `IBackgroundJobClient` (jobs are recorded, the test runs them), and `FakeCatalogImageHandler` on the
  `wardrobe-catalog-images` HttpClient.

## Recommendation rules

- **What exists:** the outfit engine (`Features/Recommendation`, design in `docs/design/outfit-engine.md`), schema `recommendation`
  (`outfits`, `outfit_items`, `outfit_feedback`, `narrations`), `UserProfile.City`, and `/api/v1/me/outfits`: `GET /daily?date=&occasion=&seed=
  &personaId=`, `GET /how-to-wear?productId=|wardrobeItemId=&count=&seed=&occasion=&personaId=`, `GET /saved`, `GET /{id}`, `POST /{id}/save`,
  `POST /{id}/worn`, `POST /{id}/feedback`, `DELETE /{id}?reason=`.
- **Rules + scores, no ML, no LLM in the generation path.** The rules are tables with a comment per entry (`OutfitRules.cs`: colour, formality,
  silhouette, pattern; `WeatherRules.cs`; `Occasions.cs`); the formula is the comment block at the top of `OutfitScoring.cs`. Change a table and its
  comment and `OutfitRulesTests` together. Everything in the engine is pure (`OutfitGenerator`, `OutfitCounter`, `OutfitScoring`); the I/O is in
  `OutfitSources` (loading) and `OutfitStore` (persisting).
- **Determinism and variety.** Same inputs and seed give the same outfits. Daily: the seed plus the day index a diverse list whose entries differ
  pairwise in ≥ 2 pieces (`PickDaily`), so "another one" (next seed) always changes two pieces when the wardrobe allows. How-to-wear: a seeded
  FNV jitter re-ranks near-equals; no companion piece in more than 2 outfits (`PickAround`); the picks are returned
  `Complete` before `NeedsOne` before `CatalogOnly` regardless of score, score order within each group. Seed 0 is the plain ranking.
- **Honesty about gaps.** `Complete` (no catalog piece besides the anchor), `NeedsOne` (one), `CatalogOnly` (more). Daily asks the catalog only when
  the wardrobe alone cannot reach the acceptance threshold (60), and then for one neutral piece; an empty wardrobe gets `kind: Shopping` ideas with
  `wardrobeEmpty: true` and a message. Gap pieces carry their cheapest offer by effective price (`GetProductEndpoint.EffectivePrice`, the product
  page's order) and a `goPath`, never a seller URL; `completionPrice` sums them. Catalog candidates come only from the narrow per-slot prefetch
  (`OutfitSources.LoadCatalogAsync`: slot categories, colour family, formality band, active with a sellable offer, audience, persona budget, newest
  30, backed by the partial index `(category_id, primary_color, formality) WHERE is_active`). Never score the catalog in bulk.
- **Persona:** the catalog convention (`personaId`, else active, else first, else none). Pieces carry their `MatchScorer` score; the persona's
  formality range, family weights, avoided colours and budget feed the context. An explicit occasion overrides the persona's band.
- **Weather:** `IWeatherProvider` (Open-Meteo, `Weather:Enabled`, env `Weather__Enabled`; cached per city/day/hour in memory, never throws, null
  on any failure); no city or no forecast → the date's season. How-to-wear ignores the day (`WeatherRules.Neutral`). Tests use
  `FakeWeatherProvider` (answers by city name: `Rainville`, `Coldville`, `Hotville`, `Istanbul`).
- **Narration never blocks.** Requests answer with `TemplateNarrator` (Turkish, ≥ 12 templates, grammar in `TurkishGrammar`) or an LLM line
  already cached for the same `OutfitSummary` hash. With `Narration:Provider = Anthropic` (env `Narration__Provider`, key `Anthropic:ApiKey`,
  model `claude-haiku-5-5`) the store enqueues `outfit-narrate` (`OutfitNarrationJob`, fire-and-forget, `[AutomaticRetry(Attempts = 0)]`),
  which calls `AnthropicNarrator` (plain `HttpClient`, one call per set, 60 output tokens per outfit, strict JSON schema, 3 s, template fallback on
  any failure, cache 7 days in `recommendation.narrations`) and updates the rows; the app sees it on `GET /{id}`.
- **How-to-wear is one engine with two callers.** `HowToWear.LoadAsync` (persona, wardrobe, owned products, dismissed hashes; once per
  request) and `HowToWear.SuggestAsync` (one anchor) serve `GET /how-to-wear` and the social `POST /posts/{id}/try`; never copy it.
- **Repeat requests reuse rows**: same user, kind, day, occasion, persona and piece hash (not dismissed) → the stored outfit. A dismissed hash is
  never suggested again for 30 days. Feedback rows are append-only; `worn` marks wardrobe pieces through `WearSummaries.RecordAsync`.
- **Counts:** `OutfitCounter` (core triples, rule components only, capped at 999) behind `OutfitCounts`, whose `PossibleOutfitsAsync`,
  `ItemOutfitsAsync` and `CompanionCountsAsync` are cached in memory under a wardrobe fingerprint (count + latest `UpdatedAt`) so any wardrobe
  change invalidates them; `GapAnalysis.Analyze` takes the engine's `unlocks` estimator (`OutfitCounts.Unlocks`).
- **Pairs** (`OutfitPairs`, pure, formula at the top of the file) have no rules of their own: `Judge` asks `ColorRules`, `FormalityRules`,
  `PatternRules` and `SilhouetteRules` about two pieces, and the score is `OutfitScoring.RuleScore`, shared with `OutfitCounter`. Two pieces of one
  slot never pair. No weather, occasion or avoided colours: `companionCount` describes the clothes. A persona only mixes in its match
  (`OutfitScoring.WithPersona`), which reorders. `outfitCount` is `OutfitCounter.CountIncluding` of the item, so the outfits of all tops add up to
  `possibleOutfits`. The `unlock` tries top, bottom, shoes and outerwear with fewer than `EnoughCompanions` (3), emptiest first, through the same
  narrow prefetch as gaps (`OutfitSources.LoadCatalogAsync`); accessories are left out because they unlock nothing. Its price, seller and `goPath`
  are one offer, the cheapest by effective price, not the `lowestPrice` of the catalog list.
- **Performance budget:** daily, how-to-wear, pairings and the wardrobe grid ≤ 300 ms p95 at 200 pieces / 50k products, measured by `OutfitPerformanceTests` on its own
  PostgreSQL container (50k rows would disturb the shared test database). Keep it passing. The tests run only with `KOMBIN_PERF=1` (skipped
  otherwise; the fixture starts nothing), which CI sets in a step of its own: a budget measured next to other tests or a local stack is noise.
  Never loosen the budget to make them pass.

## Social rules

- **What exists:** OOTD photo posts and outfit (collage) posts, confirmed shoppable tags, likes, follows, blocks, reports with a review queue,
  creators with attribution, the feed, "Dolabımla dene" (`/api/v1/posts`, `/feed`, `/users`, `/creators`, `/me/posts`, `/me/blocks`,
  `/me/creator/stats`, `/social/consent`), schema `social`, jobs `post-process` and `social-purge`. Design: `docs/design/social.md`.
- **The founder's rule: a shoppable, price-aware feed, never an Instagram clone.** Every post answers what it is, where it is cheapest and
  whether it goes with what the viewer owns. No comments, DMs, stories, video, hashtags or search over posts, no payouts, no public web pages.
- **No raw URL, ever.** Tags carry the product's current cheapest sellable offer (`OutfitSources.CheapestOffersAsync`) and a `goPath` with
  `src=feed&post=`; `PostResponses` is the only builder of post JSON. `/go` attributes only when the post is live, not private and tags the
  clicked product (`CreatorAttribution`); a creator's own click earns nothing.
- **No tag without confirmation.** Detected regions and outfit pieces are proposals (`confirmed_at` null), shown to the author only.
  `PUT /posts/{id}/publish` replaces every tag and proposal with what the author sent, confirmed. Never return an unconfirmed tag to anyone
  else, and never auto-confirm.
- **Privacy rules (photos of people are personal data; `docs/privacy-notes.md` lists every touchpoint, keep it current):**
  - posting needs the current `PostingConsent` version; versions are append-only and their text hashes are pinned in `PostingConsentTests`
    (a new text is a new version; never edit a published one). The acceptance row stores the exact text shown.
  - posts are `Private` until the author chooses `Followers` or `Public`; others see only `Live` posts (`PostRules.CanView`, the one place
    for visibility, plus `PostAccess`); a block in either direction hides everything both ways and removes follows.
  - metadata is stripped before anything reads the photo (`ImageMetadata.Strip`, JPEG/PNG/WEBP only; unparseable → refused) and the stored
    original is replaced; renditions are re-encoded by the vision service from pixels; all post objects are private (presigned, 1 h).
  - delete = tombstone at once + `social-purge` (hourly) deletes objects and rows, so "within 24 h" holds. Clicks and referral events keep the
    post ID only.
  - analytics events carry post/creator IDs and enums, never captions, labels or handles.
- **Safety before visibility.** `post-process` (fire-and-forget, `[AutomaticRetry(Attempts = 3)]`, 5/15/30 s, the count is the post's own
  `ProcessingAttempts`, 4th failure → `Failed`) maps `/v1/moderate`'s verdict with `PostRules.StatusAfterModeration`: allow → `Draft`,
  review → `NeedsReview`, block → `Blocked` + `SocialCopy.Blocked`. It never publishes; an unknown verdict counts as review. Detection
  failures only mean no proposals. Outfit posts moderate each wardrobe cutout; the strictest verdict wins. Thresholds live in `kombin-vision`.
- **Reports:** one per user and post; the 3rd distinct reporter since the last review hides a live post (`PostRules.HideAtReports`). Reviews go
  through the internal endpoints (no admin UI); approval restarts the count.
- **Feed:** `FeedRanking` is pure and documented at the top of the file; its SQL twin is `FeedRanking.BaseSql`. Change a constant in both
  and in `docs/design/social.md`. SQL picks ≤ 200 candidates (14-day window, partial index `ix_posts_feed_window`), the rest is in memory;
  the persona "≥ 60" filter applies once the persona has learned (confidence > 0, `ViewerPersona.Learned`). `SeparateAuthors` keeps one author
  from being adjacent. Budget: p95 ≤ 250 ms at 50k posts, `FeedPerformanceTests` (`KOMBIN_PERF=1`, own container, CI's perf step).
- **Creators** are set by the founder only (`PUT /internal/social/creators/{userId}`). `Creators:SharePercent` (40) is snapshotted on every
  `ReferralEvent`; the stats' estimate uses `AverageOrderValue`, `CommissionPercent` and `ConversionRatePercent` (3; the brief's formula had no
  conversion rate, 100 reproduces it) and is labelled `isEstimate`. Conversions will be imported as `ReferralKind.Conversion` rows matched by
  our click ID; nothing writes them yet.
- **Tests:** `FakeVisionClient.PostPhoto(flavor)` is a structurally valid JPEG with a GPS EXIF block; flavours `nsfw` (block), `borderline`
  (review), `no-detect`, plus the wardrobe ones. Tests run `PostProcessJob.ProcessAsync` and `SocialPurgeJob.PurgeAsync(authorId)` directly.
  The feed is global: assert on your own posts, never on absolute positions beyond the followed section.

## Commands

```bash
dotnet restore Kombin.slnx
dotnet format Kombin.slnx --verify-no-changes --no-restore     # drop --verify-no-changes to fix
dotnet build Kombin.slnx --configuration Release --no-restore
dotnet test --solution Kombin.slnx --configuration Release --no-build    # integration tests need Docker
dotnet test --project tests/Kombin.Api.Tests                            # unit tests only, no Docker
KOMBIN_PERF=1 dotnet test --project tests/Kombin.Api.IntegrationTests --configuration Release -- --filter-class "Kombin.Api.IntegrationTests.OutfitPerformanceTests" --filter-class "Kombin.Api.IntegrationTests.FeedPerformanceTests"    # latency budgets, on a quiet machine

# Run the API against a local PostgreSQL (docker/.env must define JWT_SIGNING_KEY)
docker compose --file docker/compose.yaml up --build --detach --wait db
dotnet run --project src/Kombin.Api

# Whole stack: db (pgvector/pgvector:pg18), minio + minio-init (bucket and policy), vision (built from ../kombin-vision), api
docker compose --file docker/compose.yaml up --build --detach --wait

# Migrations (local tool, version pinned in .config/dotnet-tools.json)
dotnet tool restore
dotnet ef migrations add <Name> --project src/Kombin.Api --output-dir Persistence/Migrations
dotnet ef database update --project src/Kombin.Api
```

In `Development` the API applies migrations at startup when `Database:MigrateOnStartup` is true. In every other
environment migrations are applied only with `dotnet ef`.

`Database:SeedOnStartup` (default false; true in `appsettings.Development.json` and for the compose `api` service, env
`SEED_ON_STARTUP`) seeds an empty database right after migrations: ~45 products, 90 offers, 90 days of price history and
the demo user `demo@kombin.app` / `Demo1234!` (display name `Demo Kullanıcı`). It is idempotent, and ignored with a
warning outside `Development`. The code is in `Persistence/Seed`; offers go through `OfferUpserter`, the same code as the
internal upsert endpoint.

**`Persistence/Seed/dev-catalog.json` is the only place with sample data.** Change the catalog there. Tests never depend on
it (except the seeding tests, which read it to compute expected numbers): they build their own data with unique names, so
the shared test database stays independent of the seed. `ApiFactory` switches seeding off explicitly for that reason.

## Working agreements

- **Do not abstract until the third repetition.** Copy the code twice; extract on the third occurrence (that is how
  `AuthTokenIssuer` came to exist: register, login and refresh).
- **Do not commit unless asked.** Leave the working tree for review.
- Never claim a check passed without running it.

## Explicitly out of scope (do not add)

Rate limiting (including on `/go`, `/events` and the auth endpoints), email verification, password reset, social login beyond Google and
Apple ID tokens (no OAuth redirect flow, no other providers), admin UI, MediatR/CQRS, generic
repositories, AutoMapper, email/SMS/marketing notifications, A/B testing, scraping workers, affiliate conversion import, a free-form
analytics event stream (Analytics accepts only its allow-list), and in Social: comments, DMs, stories or video, hashtags or search over
posts, follow requests, creator payouts and public web pages for posts. Features that exist: Identity (auth with a password or a Google/Apple ID token, me), Catalog (brands, sellers, categories, products, offers, price
snapshots, internal upsert, seller bootstrap and listing, offer sweep), Engagement (saved products, price alerts and their trigger feed, `/go` redirect, click
history) and Profile (user profile and measurements, personas, swipe onboarding with style cards, derived style profile,
rule-based match score, size map with hints) and Notification (devices, notification preferences, the notification feed, push
delivery through Expo, and the Hangfire jobs above) and Analytics (the allow-listed event intake, identity linking, the weekly beta
metrics and the operators' Metabase) and Wardrobe (photo items analysed by kombin-vision, catalog items, object storage, wears, stats, gap
analysis, pairings) and Recommendation (the rule-based outfit engine: daily, how-to-wear, saved outfits, feedback, weather, narration) and
Social (versioned posting consent, photo and outfit posts with moderation, metadata stripping and confirmed shoppable tags, likes, follows,
blocks, reports and review, the feed, try-with-wardrobe, creators with click attribution and earnings estimates). Learning from
clicks, saves and outfit feedback is still to come, and not Profile's job. Add other features only when a task asks for them.
