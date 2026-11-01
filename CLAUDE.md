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
- **One PostgreSQL schema per feature** (`identity`, `catalog`, ...). Tables and columns are snake_case (naming convention
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
  redirect: it records a `Click` and applies the seller's affiliate template. New endpoints must not hand out a raw
  `Offer.Url` as something to open (the existing `url` in `GET /catalog/products/{id}` offers is display data; clients
  open `/go/{offerId}`). `/go` is the one anonymous route that still reads the user: use `ICurrentUser.UserIdOrNull`
  on endpoints that work signed in or out (`UserId` throws).
- **Alert evaluation lives in the catalog upsert path.** `OfferUpserter` calls `Features/Engagement/AlertEvaluator`
  after each group commits, for every existing offer whose effective price or stock status changed. Alerts are never
  evaluated on user requests or in a separate job. The rule logic is the pure `AlertEvaluator.Evaluate`; evaluation
  failures are logged with the offer ID and must never fail the upsert. Anything that changes offers must go through
  `OfferUpserter` or alerts will not fire.
- Trigger rows (`engagement.alert_triggers`) are the notification feed; the future Notification feature reads them and
  sets `ConsumedAt`. Clicks and triggers are append-only.
- `Outbound:IpSalt` (env `Outbound__IpSalt`) salts the stored IP hash; real affiliate templates are per-seller data
  (`AffiliateUrlTemplate`, `AffiliateParams`), not code.

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
repositories, AutoMapper, push notifications and background jobs, affiliate conversion import, a generic analytics event
stream. Features that exist: Identity (auth, me), Catalog (brands, sellers, categories, products, offers, price
snapshots, internal upsert) and Engagement (saved products, price alerts and their trigger feed, `/go` redirect, click
history). Add other features only when a task asks for them.
