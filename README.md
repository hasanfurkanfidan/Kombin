# Kombin backend

Backend for Kombin, a mobile app that learns a user's style, manages their wardrobe, suggests outfits, compares prices
across Turkish retailers and redirects to retailers through affiliate links. .NET 10 Minimal API, vertical slices,
PostgreSQL 18 via EF Core. See [CLAUDE.md](CLAUDE.md) for the architecture rules and the recipe for adding a use case.

Currently implemented: **Identity** (register, login, refresh with rotation, logout, current user), **Catalog**
(products, sellers, offers, price history, plus an internal ingestion endpoint for scrapers) and **Engagement** (saved
products, price/stock alerts, and the `/go/{offerId}` affiliate redirect that records every buy click) and
**Notification** (device registration, notification preferences and feed, push delivery through Expo, background jobs on
Hangfire).

## Requirements

- .NET SDK 10.0.401 (pinned in `global.json`)
- Docker (local PostgreSQL, integration tests)

## Run

```bash
cp docker/.env.example docker/.env      # then set JWT_SIGNING_KEY and INTERNAL_API_KEY, e.g. `openssl rand -base64 48`
docker compose --file docker/compose.yaml up --build --detach --wait
curl -i http://localhost:8080/health
```

The compose stack runs the API in `Development` with `Database__MigrateOnStartup=true` and `Database__SeedOnStartup=true`,
so the schema is created and filled on first start. OpenAPI document (Development only): `http://localhost:8080/openapi/v1.json`.

### Demo data

On an empty database the API seeds a realistic Turkish catalog (45 products from 13 brands, 90 offers on Trendyol,
Hepsiburada and brand stores, 90 days of price history) and a demo user:

| Email | Password | Display name |
|---|---|---|
| `demo@kombin.app` | `Demo1234!` | Demo Kullanıcı |

```bash
curl -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json"   --data '{"email":"demo@kombin.app","password":"Demo1234!"}'
```

Seeding is idempotent (a restart only logs "already seeded") and only runs in `Development`; `Database:SeedOnStartup` is
ignored with a warning anywhere else. The data lives in `src/Kombin.Api/Persistence/Seed/dev-catalog.json`. To start over:
`docker compose --file docker/compose.yaml down --volumes`.

To run the API from the IDE or CLI against the compose database only (`appsettings.Development.json` migrates and seeds too):

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

### Engagement

Everything a signed-in user does on a product. All routes need a bearer token except `/go`.

| Method | Route | Success | Errors |
|---|---|---|---|
| PUT | `/me/saved/{productId}` | 204, idempotent | 404 `Catalog.ProductNotFound` |
| DELETE | `/me/saved/{productId}` | 204, idempotent | - |
| GET | `/me/saved?page=&pageSize=` | 200 paged, newest first; the catalog list item fields plus `isActive` and `savedAt` | - |
| GET | `/me/saved/ids` | 200 `{ productIds }` (newest 1000) | - |
| POST | `/me/alerts` | 201 alert `{ id, kind, targetPrice, sizes, isActive, triggerCount, lastTriggeredAt, lastPriceAfter, createdAt, product }` | 400 validation or `Engagement.TargetNotBelowCurrent`, 404 `Catalog.ProductNotFound`, 409 `Engagement.AlertExists` |
| GET | `/me/alerts?active=true` | 200 `{ items }`, newest first | - |
| PATCH | `/me/alerts/{id}` | 200 alert; body `{ targetPrice?, sizes?, isActive? }` | 400, 404 `Engagement.AlertNotFound`, 409 `Engagement.AlertExists` |
| DELETE | `/me/alerts/{id}` | 204; its triggers stay in the feed | 404 `Engagement.AlertNotFound` |
| GET | `/me/alerts/triggers?page=&pageSize=` | 200 paged feed, newest first | - |
| GET | `/me/clicks?page=&pageSize=` | 200 paged click history | - |
| GET | `/go/{offerId}?src=&personaId=` | 302 to the seller (anonymous allowed), `Cache-Control: no-store` | 404 `Engagement.OfferNotAvailable` |

**Buy links are always `/go/{offerId}`, never the seller's raw URL.** The redirect records a `Click` and builds the
affiliate URL from the seller's `AffiliateUrlTemplate`/`AffiliateParams` (placeholders `{url}` URL-encoded offer URL,
`{rawUrl}`, `{clickId}`, `{source}`, `{p.<key>}`). A seller without a template gets the offer URL with
`utm_source=kombin&utm_medium=app&utm_campaign=<src>&kombin_click=<clickId>` merged into its query string; the two
seeded marketplaces have none yet, real templates are configured per seller later. `src` is one of `product_detail`,
`outfit`, `saved`, `alert`, `feed`; anything else is recorded as `unknown`. Clicks store a SHA-256 of the caller's IP and
`Outbound:IpSalt` (env `Outbound__IpSalt`, `OUTBOUND_IP_SALT`; defaults to a Development-only placeholder in the compose
file, at least 16 characters and not a placeholder elsewhere), never the IP. Rate limiting on `/go` is not implemented yet.

```bash
curl -i "http://localhost:8080/api/v1/go/$OFFER_ID?src=product_detail"
```

Health: `/health/live` (process only), `/health` (also checks PostgreSQL).

Errors are `application/problem+json` with `errorCode` and `correlationId`; validation errors add an `errors` dictionary.
Send `X-Correlation-ID` (`^[A-Za-z0-9\-_.:]{1,64}$`) to choose your own correlation ID; otherwise one is generated. It is
always echoed in the response header.

### Notification

Turns what Engagement records into notifications the user receives: a price drop or restock becomes a push within a minute or
two, a weekly digest per persona goes out on Sunday evening, and the app gets a feed (the bell badge, design screen 06). Push
goes through the **Expo Push Service**: the app registers Expo push tokens, the backend never talks to FCM or APNs.

| Method | Route | Success | Errors |
|---|---|---|---|
| PUT | `/me/devices` | 204; body `{ expoPushToken, platform: Ios\|Android, appVersion?, deviceName? }`; upsert, a token moves to the caller if it belonged to someone else | 400 validation, 400 `Notification.InvalidPushToken` |
| DELETE | `/me/devices/{token}` | 204, idempotent (URL-encode the token); call it on logout | - |
| GET | `/me/notification-preferences` | 200 `{ priceAlerts, stockAlerts, recommendations, maxRecommendationsPerWeek, quietHoursStart, quietHoursEnd, timeZone, updatedAt }`, created with the defaults on first read | - |
| PUT | `/me/notification-preferences` | 200 same shape; every field optional (`clearQuietHours: true` removes the quiet hours); times are `HH:mm` in `Europe/Istanbul` | 400 validation (`maxRecommendationsPerWeek` 0-7), 400 `Notification.QuietHoursIncomplete` |
| GET | `/me/notifications?page=&pageSize=` | 200 paged feed, newest first, everything except `Failed`: `{ id, kind, title, body, data, createdAt, readAt, status }` | - |
| GET | `/me/notifications/unread-count` | 200 `{ count }` | - |
| POST | `/me/notifications/{id}/read` | 204, idempotent | 404 `Notification.NotFound` |
| POST | `/me/notifications/read-all` | 204 | - |
| POST | `/internal/notifications/test` | 200; body `{ userId, title?, body? }`; creates a `System` notification and pushes it right away; returns its status and what Expo said per device | 401, 404 `Notification.UserNotFound` |

`POST /auth/logout` also accepts an optional `expoPushToken`; when present, that device is deactivated.

`kind` is `PriceDrop`, `BackInStock`, `AnyChange`, `NewMatches` or `System`; `status` is `Pending`, `Sent` or `Suppressed` in the feed
(`Suppressed` = the user had no usable device, so nothing was pushed, but it is still in the feed). `data` always has a `deepLink`
(`kombin://product/{id}`, `kombin://matches/{personaId}`, `kombin://notifications`) plus `productId`/`offerId`/`alertId`, or `personaId`/`productIds`.

**Jobs** (Hangfire, PostgreSQL storage in the `hangfire` schema, all idempotent; the dashboard is at `/hangfire` in Development only):

| Job | Schedule | Does |
|---|---|---|
| `alert-dispatcher` | every minute | alert triggers → notifications (preference off or a repeat within 24 h: skipped; no device: kept as `Suppressed`; quiet hours: scheduled for their end) |
| `push-sender` | every minute | sends due notifications to Expo in batches of 100, one message per device; a dead device is deactivated; a failed call is retried, the 5th failure fails the notification |
| `push-receipts` | every 15 min | asks Expo what became of pushes sent 15 min - 24 h ago |
| `weekly-matches` | Sunday 19:00 `Europe/Istanbul` | top 3 products added in the last 7 days that score at least 70 for the user's active persona, at most `maxRecommendationsPerWeek` per week |

Configuration: `Hangfire__Enabled` (compose: `HANGFIRE_ENABLED`, default true), `Expo__AccessToken` (compose: `EXPO_ACCESS_TOKEN`, optional).

#### Getting a real push on a phone

The backend needs nothing beyond what is above. In the mobile app:

1. Create the Expo project id once with `npx eas init`; it writes `extra.eas.projectId` into `app.json`. Push credentials (FCM for
   Android, APNs for iOS) are configured in EAS (`eas credentials`), not here.
2. On a **physical device** (simulators cannot receive remote pushes), ask for permission and read the token with
   `Notifications.getExpoPushTokenAsync({ projectId })` (from `expo-notifications`).
3. Send it to `PUT /api/v1/me/devices` after login with the platform, and to `DELETE /api/v1/me/devices/{token}` (or in the logout body) on logout.
4. Check it end to end without waiting for a price change:

```bash
curl -X POST http://localhost:8080/api/v1/internal/notifications/test   -H "X-Internal-Key: $INTERNAL_API_KEY" -H "Content-Type: application/json"   --data '{"userId":"<the user id from GET /me>","title":"Merhaba","body":"Bu bir test bildirimidir."}'
```

A fake or stale token comes back as `status: "Suppressed"` / `failureReason: "DeviceNotRegistered"` and the device is deactivated;
any other Expo error is `Failed` with the reason. Only if "enhanced push security" is turned on for the Expo project, set
`Expo__AccessToken` (an Expo access token) on the backend.

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

### Engagement decisions

- **Alerts are evaluated inside the upsert path.** After a group of offers is committed, `OfferUpserter` calls
  `AlertEvaluator` for every existing offer whose effective price or stock status changed. An evaluation failure is
  logged with the offer ID and never fails or rolls back the upsert (it runs after the commit).
- **Rules**, all on the *effective* price (price + shipping). `PriceDrop` fires when the offer can be bought and its price
  crosses the target from above (a price seen while out of stock counts as "above"); `BackInStock` when stock goes from
  `OutOfStock`/`Unknown` to `InStock`/`LowStock`, and with `sizes` only if one of them is in the offer's
  `AvailableSizes` (case-insensitive); `AnyChange` on a price move of at least 1 % or any stock status change. A new or
  re-linked offer has no previous state and fires nothing. Firing keeps the alert active.
- **Per offer, not per product.** An alert fires for the offer that changed, so a second seller dropping below a target that
  another seller already beats still fires once.
- **`lowestPrice`** in every product summary is the catalog's (sticker price of the cheapest buyable offer), while the
  `PriceDrop` target is checked against the lowest *effective* price. A product with no buyable offer accepts any target.
- **Saved list** items for a product nobody sells report `offerCount: 0` and `lowestPrice: 0`.
- **Click IDs** are reserved from the identity sequence before the insert, because the ID goes into the redirect URL; the
  `clicks` table is therefore only ever inserted into.
- **Behind a proxy** the click's IP hash is that of the proxy until forwarded headers are configured.

## TODO

- Rate limiting on the auth endpoints (out of scope for the foundation task).
- Catalog: deactivating offers that stop being seen (`Offer.IsActive`, and `Product.IsActive` when none remain) needs a
  sweep job or an internal endpoint; nothing sets them to false yet except re-linking.
- Catalog: full-text search (today `ILIKE`), entity resolution/image similarity for products without a `modelCode`,
  copying images to our own storage, pgvector embeddings, TimescaleDB for `price_snapshots`.
- Engagement: rate limiting and bot filtering on `/go`, affiliate conversion/postback import, real affiliate templates per
  seller, forwarded-headers setup.
- Notification: the first run of `alert-dispatcher` on a database that already has unconsumed triggers turns every one of them
  into a notification (the oldest 500 per minute); mark old triggers consumed before enabling Hangfire there. The Hangfire
  dashboard has no authentication, which is why it exists in Development only.
