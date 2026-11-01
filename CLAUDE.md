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
- **One PostgreSQL schema per feature** (`identity`, `catalog`, `engagement`, `profile`, ...). Tables and columns are snake_case (naming convention
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

Cross-cutting code lives in `src/Kombin.Api/Common/` (Results, Http, Auth, Validation, Pagination, Time, Health). Development
seed data lives in `src/Kombin.Api/Persistence/Seed/`.

Internal endpoints (called by workers, not the app) live under `/api/v1/internal/...` and use `.RequireInternalKey()`. The ones that
exist: `POST /internal/catalog/offers/upsert`, `POST /internal/catalog/offers/sweep`, `GET /internal/catalog/sellers/{sellerSlug}/offers`,
`PUT /internal/catalog/sellers/{slug}` (see Catalog ingestion rules), `POST /internal/notifications/test` and
`GET /internal/analytics/weekly` (see Analytics rules).

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
- `error.ToHttpResult()` / `result.ToHttpResult()` map `ErrorType` to 400/401/403/404/409/500 as `application/problem+json`
  with an `errorCode` extension and the `correlationId`.
- Input validation uses FluentValidation through `ValidationFilter`: failures become a 400 problem with an `errors`
  dictionary (`{ "email": ["..."] }`, camelCase keys). Do not validate inside handlers what a validator can express.
- Every response, including errors, carries `X-Correlation-ID`.

## Catalog ingestion rules

External Python workers (a separate repo) push the catalog through the internal endpoints below, all behind `X-Internal-Key`.

- **`POST /internal/catalog/offers/upsert`** (`OfferUpserter`, up to 500 items). The response is
  `{ created, updated, snapshotsWritten, results: [{ index, externalId, productId, offerId, created }], errors }`: `results` holds the
  stored items, `errors` the rejected ones, and together they account for every index. `product` may carry `brandWebsiteUrl` /
  `brandLogoUrl`: they are set when the brand is created and filled in later only while the brand has none, never overwritten (this
  also happens on an existing offer). `imageUrls` holds at most 6 entries. Image, brand website and seller website URLs must be
  absolute http(s) with a host, no credentials and no whitespace (`UpsertOfferItemValidator.IsPublicHttpUrl`).
- **`PUT /internal/catalog/sellers/{slug}`** with `{ name, kind, websiteUrl, affiliateNetwork?, affiliateUrlTemplate?, affiliateParams?,
  isActive }` registers or updates a seller before the first upsert. It is a full replacement: optional fields left out are cleared, and
  `isActive` defaults to true. `kind` is `Marketplace` or `BrandStore`, the slug is kebab-case, and a template must contain `{url}` or
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
  `goPath`. `Offer.Url` stays in the database for the redirect and the ingestion workers only. `/go` is the one anonymous route that still reads the user: use `ICurrentUser.UserIdOrNull`
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
  touch their own rows. Every job has `[DisableConcurrentExecution(60)]` and `[AutomaticRetry(Attempts = 0)]` (the next run is the retry).

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
  Catalog's `offer-sweep` and `UseAnalyticsJobs` the Analytics' `weekly-metrics` right after it, behind the same flag) and, **in Development only**,
  maps the dashboard at `/hangfire` (open, no auth, unmapped in every other environment). Nothing happens unless `Hangfire:Enabled` is
  true (compose sets it; tests set it to false; `Development` defaults to true).
- **Copy is Turkish and lives in `Features/Notification/Copy.cs`** (money as `2.149 ₺` / `849,90 ₺`, formatted by hand because the runtime
  image may run with invariant globalization). Clients read `Notification.Data.deepLink`; do not change its shape without the app team.

## Analytics rules

- **What exists:** `POST /api/v1/events` (public), the `analytics.events` table, `IAnalyticsIdentityLinker`, the `weekly-metrics` job with
  `analytics.weekly_metrics` and `GET /internal/analytics/weekly`, `docs/metrics.sql`, and an optional Metabase in compose.
- **The allow-list is the privacy guarantee.** `AnalyticsEventCatalog` (pure) lists every event name and, per event, its properties with
  their types; the validator rejects anything else. A new event or property is a catalog entry plus a README row, nothing more. Property
  values are GUIDs, integers, enum names or short identifiers, never free text (`search` sends `queryLength`, never the query). Keys named
  like personal data (`email`, `name`, `phone`, `address`, `token`) are refused on every event, and so is any key not listed for the event.
- **A bad event fails the whole batch.** An unknown name is 400 `Analytics.UnknownEvent` (the validation filter reports a rule's dotted error
  code instead of `Validation.Failed`); other violations are 400 `Validation.Failed` with `events[i].properties.key` entries. Only two
  things are per-event and non-fatal, reported in `rejected` of the 202: `duplicate` (same `sessionId`, `name` and client `occurredAt`
  earlier in the batch; across batches nothing is deduplicated) and `missing_identity` (anonymous caller without `anonymousId`).
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

## Commands

```bash
dotnet restore Kombin.slnx
dotnet format Kombin.slnx --verify-no-changes --no-restore     # drop --verify-no-changes to fix
dotnet build Kombin.slnx --configuration Release --no-restore
dotnet test --solution Kombin.slnx --configuration Release --no-build    # integration tests need Docker
dotnet test --project tests/Kombin.Api.Tests                            # unit tests only, no Docker

# Run the API against a local PostgreSQL (docker/.env must define JWT_SIGNING_KEY)
docker compose --file docker/compose.yaml up --build --detach --wait db
dotnet run --project src/Kombin.Api

# Whole stack
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

Rate limiting (including on `/go` and `/events`), email verification, password reset, social login, admin UI, MediatR/CQRS, generic
repositories, AutoMapper, email/SMS/marketing notifications, A/B testing, scraping workers, affiliate conversion import, a free-form
analytics event stream (Analytics accepts only its allow-list). Features that exist: Identity (auth, me), Catalog (brands, sellers, categories, products, offers, price
snapshots, internal upsert, seller bootstrap and listing, offer sweep), Engagement (saved products, price alerts and their trigger feed, `/go` redirect, click
history) and Profile (user profile and measurements, personas, swipe onboarding with style cards, derived style profile,
rule-based match score, size map with hints) and Notification (devices, notification preferences, the notification feed, push
delivery through Expo, and the Hangfire jobs above) and Analytics (the allow-listed event intake, identity linking, the weekly beta
metrics and the operators' Metabase). Learning from clicks/saves and outfit generation are the Recommendation task,
not Profile. Add other features only when a task asks for them.
