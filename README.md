# Kombin backend

Backend for Kombin, a mobile app that learns a user's style, manages their wardrobe, suggests outfits, compares prices
across Turkish retailers and redirects to retailers through affiliate links. .NET 10 Minimal API, vertical slices,
PostgreSQL 18 via EF Core. See [CLAUDE.md](CLAUDE.md) for the architecture rules and the recipe for adding a use case.

Currently implemented: **Identity** (register, login, refresh with rotation, logout, current user) and **Catalog**
(products, sellers, offers, price history, plus an internal ingestion endpoint for scrapers).

## Requirements

- .NET SDK 10.0.401 (pinned in `global.json`)
- Docker (local PostgreSQL, integration tests)

## Run

```bash
cp docker/.env.example docker/.env      # then set JWT_SIGNING_KEY and INTERNAL_API_KEY, e.g. `openssl rand -base64 48`
docker compose --file docker/compose.yaml up --build --detach --wait
curl -i http://localhost:8080/health
```

The compose stack runs the API in `Development` with `Database__MigrateOnStartup=true`, so the schema is created on first
start. OpenAPI document (Development only): `http://localhost:8080/openapi/v1.json`.

To run the API from the IDE or CLI against the compose database only:

```bash
docker compose --file docker/compose.yaml up --detach --wait db
dotnet run --project src/Kombin.Api
```

## Test

```bash
dotnet test --solution Kombin.slnx --configuration Release
```

Integration tests start their own PostgreSQL container with Testcontainers, so Docker must be running.

## API (all under `/api/v1`)

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| POST | `/auth/register` | - | 201 `{ accessToken, refreshToken, expiresIn }` | 400 validation, 409 `Identity.EmailTaken` |
| POST | `/auth/login` | - | 200 same shape | 400, 401 `Identity.InvalidCredentials` |
| POST | `/auth/refresh` | - | 200 same shape; old token revoked | 401 `Identity.InvalidRefreshToken` |
| POST | `/auth/logout` | Bearer | 204 | 401 |
| GET | `/me` | Bearer | 200 `{ id, email, displayName, createdAt }` | 401 |

### Catalog

Public reads need no auth (the app browses before login).

| Method | Route | Success | Errors |
|---|---|---|---|
| GET | `/catalog/categories` | 200 tree `[{ id, slug, name, children }]` sorted by `sortOrder` | - |
| GET | `/catalog/brands?query=&page=&pageSize=` | 200 paged `{ items: [{ id, name, slug, logoUrl, websiteUrl }], page, pageSize, totalCount, totalPages }`; `query` is a case-insensitive contains match, names starting with it first | - |
| GET | `/catalog/products?query=&categorySlug=&brandSlug=&color=&audience=&minPrice=&maxPrice=&sort=&page=&pageSize=` | 200 paged list of active products with a sellable offer | 400 invalid `sort`/`color`/`audience`, 404 `Catalog.CategoryNotFound`, 404 `Catalog.BrandNotFound` |
| GET | `/catalog/products/{id}` | 200 product, images, active offers by effective price, `priceSummary` | 404 `Catalog.ProductNotFound` |
| GET | `/catalog/products/{id}/price-history?days=90` | 200 `[{ offerId, seller, points: [{ date, minPrice, stockStatus }] }]`, one point per UTC day | 404 `Catalog.ProductNotFound` |
| POST | `/internal/catalog/offers/upsert` | 200 `{ created, updated, snapshotsWritten, errors: [{ index, externalId, errorCode, message }] }` | 400 validation or `Catalog.BatchTooLarge`, 401 `Catalog.InvalidInternalKey`, 404 `Catalog.SellerNotFound` |

`sort` is `relevance` (default, newest first for now), `priceAsc`, `priceDesc` or `newest`; enum filters are case-insensitive.
`categorySlug` includes descendants. Category slugs are path-style (`ust-giyim`, `ust-giyim/blazer`).

Internal endpoints require the header `X-Internal-Key` to equal `Internal:ApiKey` (env `Internal__ApiKey`, `INTERNAL_API_KEY`
in `docker/.env`). The key must be at least 32 characters; the `dev-only-...`/`change-me` placeholders are only accepted
in Development, and the API refuses to start otherwise.

```bash
curl -X POST http://localhost:8080/api/v1/internal/catalog/offers/upsert \
  -H "X-Internal-Key: $INTERNAL_API_KEY" -H "Content-Type: application/json" --data-binary @batch.json
```

Health: `/health/live` (process only), `/health` (also checks PostgreSQL).

Errors are `application/problem+json` with `errorCode` and `correlationId`; validation errors add an `errors` dictionary.
Send `X-Correlation-ID` (`^[A-Za-z0-9\-_.:]{1,64}$`) to choose your own correlation ID; otherwise one is generated. It is
always echoed in the response header.

## Decisions worth knowing

- **Password hashing:** `PasswordHasher<T>` from the framework (`Microsoft.AspNetCore.Identity`, PBKDF2-HMAC-SHA512,
  100k iterations, auto-rehash on login). No Identity tables, no `UserManager`.
- **Email:** stored trimmed and lower-cased with a unique index (no `citext` extension needed).
- **Refresh tokens:** 256-bit opaque random values, only their SHA-256 is stored. Rotation links the old token to its
  successor through `ReplacedByTokenId`. Presenting an already-revoked token revokes **all** active refresh tokens of
  that user (the whole account is signed out everywhere). Parallel refreshes of the same token are serialized by an
  optimistic concurrency token on PostgreSQL's `xmin`, so exactly one wins.
- **Logout** revokes only the caller's own token and is idempotent (an unknown token still returns 204).
- **Logging:** Serilog to the console, with the correlation ID on every event. `GlobalExceptionHandler` is the single place
  that logs an unhandled exception (once, at Error), so EF Core's own command/query/update error categories are muted in
  `appsettings.json`; otherwise one failing query would produce several Error lines.
- **Migrations** are never applied automatically outside `Development`; use `dotnet ef database update`.

### Catalog decisions

- **Canonical products.** One `Product` per real-world item, one `Offer` per seller listing. An upsert resolves the product
  in this order: existing offer by `(seller, externalId)` (kept on its product; an explicit, different
  `canonicalProductId` re-links it), then `canonicalProductId`, then the brand's product with the same `modelCode`
  (trimmed and upper-cased on write so "mv-100" and "MV-100" match), otherwise a new product (brand created if unknown,
  slug from the name with `-2`, `-3`, ... on collision). Products without a `modelCode` are never merged automatically.
- **Brand stores** are not seeded. An unknown `sellerSlug` ending in `-store` is created as a `BrandStore` when it equals
  `<slug of the item's brandName>-store` (so `Mavi` -> `mavi-store`); any other unknown seller is a 404.
- **Validation vs per-item errors.** Malformed input (bad enum, price, URL, ...) fails the whole request with a 400 whose
  `errors` keys are indexed (`items[3].price`). Problems that depend on stored data (unknown category, unknown canonical
  product, brand-store mismatch) are reported per item in `errors` of a 200 response.
- **Transactions.** Items are processed in groups of 50, one transaction per group. If a group throws, it is rolled back and
  its items are re-run one by one, so only the failing item is reported (`Catalog.ItemFailed`).
- **Snapshots** are written when price, original price or stock status changed, or the last snapshot is older than 24 hours.
- **"Sellable" offers** (list, lowest price, summary) are active offers that are `InStock` or `LowStock`; `Unknown` does not count.
- **Effective price** = price + shipping. Shipping is 0 when the price reaches `freeShippingThreshold` or when no shipping
  cost is known.
- **Defaults** for optional product attributes on creation: `pattern=Other`, `fit=Unknown`, `formality=3`, all four seasons.
- **Images** are added (de-duplicated by URL) when an offer is created or re-linked, not on plain re-observation.
- **Enums** are stored as strings in the database and travel as names in JSON.

## TODO

- Rate limiting on the auth endpoints (out of scope for the foundation task).
- Catalog: deactivating offers that stop being seen (`Offer.IsActive`, and `Product.IsActive` when none remain) needs a
  sweep job or an internal endpoint; nothing sets them to false yet except re-linking.
- Catalog: full-text search (today `ILIKE`), entity resolution/image similarity for products without a `modelCode`,
  copying images to our own storage, pgvector embeddings, TimescaleDB for `price_snapshots`, affiliate link wrapping.
