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

Internal endpoints (called by workers, not the app) live under `/api/v1/internal/...` and use `.RequireInternalKey()`.

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
  `OfferUpserter` or alerts will not fire.
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
  migrations and the seeds) is what first creates the `hangfire` schema, registers the recurring jobs and, **in Development only**,
  maps the dashboard at `/hangfire` (open, no auth, unmapped in every other environment). Nothing happens unless `Hangfire:Enabled` is
  true (compose sets it; tests set it to false; `Development` defaults to true).
- **Copy is Turkish and lives in `Features/Notification/Copy.cs`** (money as `2.149 ₺` / `849,90 ₺`, formatted by hand because the runtime
  image may run with invariant globalization). Clients read `Notification.Data.deepLink`; do not change its shape without the app team.

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

Rate limiting (including on `/go`), email verification, password reset, social login, admin UI, MediatR/CQRS, generic
repositories, AutoMapper, email/SMS/marketing notifications, A/B testing, scraping workers, affiliate conversion import, a generic
analytics event stream. Features that exist: Identity (auth, me), Catalog (brands, sellers, categories, products, offers, price
snapshots, internal upsert), Engagement (saved products, price alerts and their trigger feed, `/go` redirect, click
history) and Profile (user profile and measurements, personas, swipe onboarding with style cards, derived style profile,
rule-based match score, size map with hints) and Notification (devices, notification preferences, the notification feed, push
delivery through Expo, and the Hangfire jobs above). Learning from clicks/saves and outfit generation are the Recommendation task,
not Profile. Add other features only when a task asks for them.
