# Kombin backend

Backend for Kombin, a mobile app that learns a user's style, manages their wardrobe, suggests outfits, compares prices
across Turkish retailers and redirects to retailers through affiliate links. .NET 10 Minimal API, vertical slices,
PostgreSQL 18 via EF Core. See [CLAUDE.md](CLAUDE.md) for the architecture rules and the recipe for adding a use case.

Currently implemented: **Identity** (register, login, refresh with rotation, logout, current user), **Catalog**
(products, sellers, offers, price history, plus an internal ingestion endpoint for scrapers) and **Engagement** (saved
products, price/stock alerts, and the `/go/{offerId}` affiliate redirect that records every buy click) and
**Notification** (device registration, notification preferences and feed, push delivery through Expo, background jobs on
Hangfire) and **Analytics** (an event intake for the app, the four beta metrics, an optional Metabase for operators) and **Wardrobe**
(a garment from a phone photo in a few seconds: background removal and tagging by `kombin-vision`, images in MinIO, wear log, stats
and a rule-based gap analysis with catalog suggestions).

## Requirements

- .NET SDK 10.0.401 (pinned in `global.json`)
- Docker (local PostgreSQL, integration tests)
- The `kombin-vision` repository checked out next to this one (`../kombin-vision`): compose builds the vision service from it

## Run

```bash
cp docker/.env.example docker/.env      # then set JWT_SIGNING_KEY and INTERNAL_API_KEY (`openssl rand -base64 48`) and VISION_INTERNAL_KEY
docker compose --file docker/compose.yaml up --build --detach --wait
curl -i http://localhost:8080/health
```

The stack is `db` (PostgreSQL 18 with pgvector), `minio` (object storage, S3 API on 9000, console on `127.0.0.1:9001`), `minio-init`
(one-shot: creates the `kombin` bucket and its public-read policy for processed images), `vision` (`kombin-vision`, built from
`../kombin-vision`, `127.0.0.1:8092`, 4 GB memory limit; it takes 40-110 s to load its models) and `api`. A standalone
`kombin-vision` compose stack uses the same host port 8092; stop it first.

**Upgrading an existing volume:** the database image changed from `postgres:18-alpine` to `pgvector/pgvector:pg18` (Debian). The data
directory is compatible, but text indexes built under Alpine's musl collation must be rebuilt once:

```bash
docker compose --file docker/compose.yaml exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "REINDEX DATABASE $POSTGRES_DB;"'
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

The latency budgets (`OutfitPerformanceTests`, 300 ms p95) are skipped unless `KOMBIN_PERF=1` is set: a laptop that also runs vision and MinIO
measures itself. CI runs them in a step of their own. To run them by hand, on a quiet machine and on their own:

```bash
KOMBIN_PERF=1 dotnet test --project tests/Kombin.Api.IntegrationTests --configuration Release -- --filter-class "Kombin.Api.IntegrationTests.OutfitPerformanceTests"
```

## API (all under `/api/v1`)

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| POST | `/auth/register` | - | 201 `{ accessToken, refreshToken, expiresIn }`; body `{ email, password, displayName, anonymousId? }` | 400 validation, 409 `Identity.EmailTaken` |
| POST | `/auth/login` | - | 200 same shape; body `{ email, password, deviceName?, anonymousId? }` | 400, 401 `Identity.InvalidCredentials` |
| POST | `/auth/refresh` | - | 200 same shape; old token revoked | 401 `Identity.InvalidRefreshToken` |
| POST | `/auth/logout` | Bearer | 204 | 401 |
| GET | `/me` | Bearer | 200 `{ id, email, displayName, createdAt }` | 401 |

### Profile

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| GET | `/me/profile` | Bearer | 200 `{ audience, birthYear, heightCm, weightKg, chestCm, waistCm, hipCm, shoeSizeEu, city, activePersona, personaCount, onboardingCompleted, onboardingCompletedAt }`; created on first use | 401 |
| PUT | `/me/profile` | Bearer | 200 same shape; body any of `{ audience, birthYear, heightCm, weightKg, chestCm, waistCm, hipCm, shoeSizeEu, city }` | 400 validation, 401 |

`PUT /me/profile` follows the seller PUT: **a field left out is unchanged, an explicit null clears it**, so a client can send only what it
changes. `audience` is the exception: a null keeps it too, a profile always has one. `city` (at most 80 characters of letters, spaces, dots,
apostrophes and hyphens; a blank clears it like a null) is where the outfit engine reads the weather; without one, outfits go by the season.

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

List items carry `cheapestSeller { name, slug }`: the seller of the offer that defines `lowestPrice`. The OpenAPI document
(`/openapi/v1.json`, Development) is generated from the endpoints and records, so it follows.

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
The push payload's `data` additionally carries `notificationId` and `kind`, so the app can report `notification_open` for exactly that row.

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

### Analytics

Product analytics for the beta: the app reports what people do, an allow-list keeps personal data out, and a weekly job turns the
events into the four beta metrics. Events are for operators; nothing in the app reads them back.

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| POST | `/events` | optional bearer | 202 `{ accepted, rejected: [{ index, reason }] }` | 400 validation (malformed body only) |
| GET | `/internal/analytics/weekly?from=` | internal key | 200 `{ items: [{ weekStart, metric, value, numerator, denominator, computedAt }] }`, newest week first | 401 |

`POST /events` takes `{ events: [1-50] }`; each event is `{ name, properties?, sessionId, platform: Ios|Android, appVersion, occurredAt,
anonymousId? }`. A valid bearer token attributes the batch to the user; an anonymous caller must send `anonymousId` (a stable random id the
app generates before login, at most 64 characters of `A-Za-z0-9-_.:`) on every event, or the event is rejected as `missing_identity`.

Events are judged one by one: a bad event is listed in `rejected` with its index in the request and a `reason`, and the rest of the batch is
stored. Only a malformed body is a 400 (`Validation.Failed`, with an `errors` dictionary): no `events`, none or more than 50, a null event,
or a bad `sessionId`, `platform`, `appVersion`, `occurredAt` or `anonymousId`.

| `reason` | The event |
|---|---|
| `unknown_event` | has a name outside the allow-list below |
| `invalid_properties` | has a property that is not listed for it, is missing, has the wrong type or value, or is nested; has properties over 2 KB in total; or has a key named like personal data (`email`, `name`, `phone`, `address`, `token`) |
| `missing_identity` | was sent by an anonymous caller without `anonymousId` |
| `duplicate` | repeats an earlier event of the same batch (same `sessionId`, `name` and `occurredAt`) |

- **Names and properties come from an allow-list** (`AnalyticsEventCatalog`), so a client that is newer than the backend only loses the
  events it invented, and the rest of its batch still counts.

  | Event | Properties |
  |---|---|
  | `app_open`, `app_background` | - |
  | `screen_view` | `screen` |
  | `product_view` | `productId`, `personaId?`, `source` |
  | `product_list_view` | `sort` (a catalog sort), `filters` (comma-separated filter names, may be empty) |
  | `swipe` | `personaId`, `decision` (`Like`, `Dislike` or `Skip`) |
  | `onboarding_step` | `step`, `action` (`next`, `skip` or `done`) |
  | `alert_created` | `kind` (`PriceDrop`, `BackInStock` or `AnyChange`) |
  | `buy_click` | `offerId`, `source` |
  | `search` | `queryLength` (0-200; never the text) |
  | `notification_open` | `notificationId`, `kind` |
  | `wardrobe_add_start` | `source` (`camera`, `library` or `catalog`) |
  | `wardrobe_add_done` | `status` (`Ready`, `Failed` or `Timeout`), `durationMs` (0-3600000) |
  | `outfit_view` | `kind`, `status` (the outfit's completeness: `Complete`, `NeedsOne` or `CatalogOnly`), `occasion?` |
  | `outfit_shuffle`, `outfit_gap_click` | - |
  | `outfit_feedback` | `action` (`Save`, `Dismiss`, `Worn` or `Shuffle`), `reason?` (a dismiss reason) |

  Values are GUIDs, integers, enum names or short identifiers (letters, digits and `_ . : / -`), never free text.
- **A wrong clock does not cost the event.** `occurredAt` more than 7 days old or more than 5 minutes ahead is replaced by the server's
  receive time.
- **Repeats** are only detected within a batch; a client that resends a whole batch after a lost response will store it twice.
- **Linking.** `POST /auth/login` and `POST /auth/register` accept an optional `anonymousId`; the events that device sent while anonymous
  (`user_id` still null) are attached to the user. Events that already belong to a user are never taken over.

Metrics: [`docs/metrics.sql`](docs/metrics.sql) holds the four queries as plain SQL (cohort window as parameters, runnable with `psql -v`);
the `weekly-metrics` job (Monday 08:00 `Europe/Istanbul`) runs that same file for the cohorts of the last four weeks and upserts
`analytics.weekly_metrics`, which `GET /internal/analytics/weekly` serves. A cohort is the people whose first `app_open` fell in a week.

| Metric | Value |
|---|---|
| `d7_retention` | share that opened the app again on day 6, 7 or 8 after the first open (calendar days, Istanbul) |
| `alert_rate` | share with at least one alert (`engagement.price_alerts`, or an `alert_created` event) |
| `click_rate` | share with at least one signed-in buy click (`engagement.clicks`) |
| `onboarding_completion` | of those who started onboarding, share that sent `onboarding_step` with `action: done` |

Values are percentages (0-100) "as of `computedAt`". `d7_retention` can only be known days after a cohort ends, so each run also
revisits the three weeks before last week: a young cohort's row counts only the members whose day 8 is over (its `denominator` shows
how many) and is completed by the next runs. A metric whose cohort is empty has no row.

#### Metabase (operators only)

`docker/compose.yaml` has an optional `metabase` service for dashboards over the analytics, engagement and catalog schemas. It is behind a
compose profile, so `up` (and CI's smoke test) never starts it, and it listens on `127.0.0.1:3000` (`METABASE_PORT`) only. Its own data
(users, questions, dashboards) is an embedded H2 file in the `kombin-metabase-data` volume.

```bash
docker compose --file docker/compose.yaml --profile metabase up --detach metabase
```

Metabase connects to the database as `metabase_ro`, a read-only role created by `docker/postgres/metabase_ro.sql`, which the `db` service
mounts into `docker-entrypoint-initdb.d`. PostgreSQL runs it only when it creates an empty data directory, so **a volume that already
exists needs the role created by hand, once**:

```bash
docker compose --file docker/compose.yaml exec -T db sh -c 'psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB"' < docker/postgres/metabase_ro.sql
```

First run: open http://localhost:3000, create the admin account, then **Add your data** -> PostgreSQL with host `db`, port `5432`,
database `kombin` (your `POSTGRES_DB`), user `metabase_ro`, password `metabase_ro_dev_password`. The role reads the `analytics`,
`engagement` and `catalog` schemas only (not `identity`, `notification`, `profile` or `hangfire`) and its transactions are read-only. The
password is a development placeholder: change it with `ALTER ROLE metabase_ro PASSWORD '...'` before pointing Metabase at real data.
A good first dashboard is `analytics.weekly_metrics` (a line per `metric`, `week_start` on the x axis).

### Wardrobe

**The rule: a user adds a garment from a photo in about three seconds, with at most one tap to confirm.** The server does the work
(background removal, category, colors, pattern), the user only corrects. All routes are under `/me/wardrobe` and need a bearer token.

| Method | Route | Success | Errors |
|---|---|---|---|
| POST | `/items/upload-url` | 201 `{ itemId, uploadUrl, headers, expiresAt }`; body `{ contentType, sizeBytes }` (jpeg, png, webp, heic/heif, at most 8 MB). Creates a `Processing` item; PUT the photo to `uploadUrl` with exactly `headers` within 15 minutes | 400 validation |
| POST | `/items/{id}/process` | 202 `{ itemId, status }`; enqueues `wardrobe-analyze`. Idempotent: no second job while processing, a ready item is left alone, a failed one starts over | 400 `Wardrobe.ImageTooLarge`, 404 `Wardrobe.ItemNotFound`, 409 `Wardrobe.UploadMissing` / `Wardrobe.ItemArchived` / `Wardrobe.NothingToProcess` |
| GET | `/items/{id}` | 200 item: attributes, `thumbUrl`, `cutoutUrl` (public), `originalUrl` (15-minute presigned link, only when there is no cutout), `needsCategory`, `warnings`, `failureReason`, `analysis` (candidates and confidences), `wearCount`, `lastWornAt`, `confirmedAt` | 404 |
| PATCH | `/items/{id}` | 200 item. Any of `name, categorySlug, primaryColor, secondaryColor, pattern, fit, formality, seasons, brandId, size, purchasePrice, purchasedAt`; `clear: ["name", "secondaryColor", "brandId", "size", "purchasePrice", "purchasedAt"]` empties fields. `{}` is valid: the first PATCH of a ready item is the user's confirmation (`confirmedAt`); it also makes a failed item ready | 400 validation, `Wardrobe.UnknownCategory`, `Wardrobe.UnknownBrand`; 404; 409 `Wardrobe.ItemProcessing` / `Wardrobe.ItemArchived` |
| POST | `/items/from-product` | 201 item (200 with the existing item when the product is already in the wardrobe); body `{ productId, size? }`. A `Ready`, confirmed item with the product's attributes; its primary image is copied into storage as cutout and thumbnail, without background removal | 400, 404 `Catalog.ProductNotFound` |
| GET | `/items?category=&color=&archived=&page=&pageSize=` | 200 paged summaries, newest first; `category` includes children; uploads never processed are left out. Each carries `companionCount`, the wardrobe pieces it pairs with ("5 parçayla gider"); 0 for an item that cannot be worn (yet) | 400 invalid `color` |
| GET | `/items/{id}/pairings?personaId=&limit=` | 200 `{ itemId, personaId, companions: [{ slot, total, items: [{ item, score, reason }] }], outfitCount, unlock }`, see Pairings below | 400 `limit` outside 1-20; 404 `Recommendation.WardrobeItemNotFound` (also archived, unfinished or uncategorised), `Profile.PersonaNotFound` |
| DELETE | `/items/{id}` | 204, archives (soft, idempotent); `wardrobe-cleanup` deletes the images 30 days later | 404 |
| POST | `/items/{id}/wear` | 200 `{ itemId, wornOn, wearCount, lastWornAt }`; body optional `{ wornOn? }` (default today in `Europe/Istanbul`); one row per day | 400 future or before 2000, 404, 409 archived |
| DELETE | `/items/{id}/wear/{yyyy-MM-dd}` | 200 same shape with `wornOn: null`, idempotent | 404 |
| GET | `/stats` | 200 `{ itemCount, byCategory: [{ slug, count }], byColor: [{ color, count }], unwornFor180Days, mostWorn: [3], leastWorn: [3], possibleOutfits }`; `possibleOutfits` is the outfit engine's count of complete core outfits, capped at 999 | - |
| GET | `/gaps` | 200 `{ personaId, gaps: [{ kind, message, categorySlug, color, formality, band, unlocksEstimate }], suggestedProducts: [3] }` | - |

**Photo flow.** `upload-url` → the app PUTs the photo straight to storage → `process` → the `wardrobe-analyze` job (Hangfire, started at
once through PostgreSQL LISTEN/NOTIFY) sends the original to `kombin-vision` (`POST /v1/garment/analyze`), stores the cutout (PNG) and
thumbnail (WEBP), and sets category, colors, pattern, a first guess at formality and seasons, the 512-d embedding and the analysis JSON →
`Ready`. The app polls `GET /items/{id}`. The category is taken only when the top candidate reaches 0.35; below that the item is ready
with `needsCategory: true` and the app shows the three candidates. A photo that is not a garment is still ready, with the `not_garment`
warning. A refused image (`unsupported_image`) fails at once; an unreachable vision service is retried three times (5, 15, 30 s), then
the item is `Failed`. Measured on the compose stack on a development laptop: ~4.4 s from `process` to `Ready` with a warm vision service
(of which the analysis is ~3.1 s), ~11 s for the first photo after the vision service started.

**Gaps** (`Features/Wardrobe/GapAnalysis.cs`, rules documented at the top of the file): for the active persona (the first one when none is
active), tops, bottoms and shoes are counted by formality band (1-2 casual, 3 smart casual, 4-5 formal) and color family; a group missing
in a band, or with no neutral color there, is a gap, and so is having no outerwear when the persona dresses for autumn or winter. The
suggested color is a neutral the persona prefers or learned from swipes. `unlocksEstimate` is the number of outfits the missing piece would
complete (e.g. bottoms × shoes for a top). At most 5 gaps, biggest unlock first. `suggestedProducts` fill the first gap: active, in-stock
catalog products of that category, color and band, not already in the wardrobe, with a persona `matchScore` of at least 60.

**Pairings** ("what goes with this", `Features/Recommendation/OutfitPairs.cs`, rules documented at the top of the file) answer what a user
wants when tapping a garment; wear tracking stays in the data for the engine.

- `companions` has one group per slot other than the item's own (`Top`, `Bottom`, `Outerwear`, `Shoes`, `Accessory`, in that order, empty ones
  included). `items` holds the best `limit` (default 6) as a wardrobe summary with a 0-100 `score` and a `reason` (`neutral`, `tonal`,
  `complementary`, `analogous`, `statement`); `total` counts them all, and the totals add up to the grid's `companionCount`. Pieces the outfit rules
  reject are left out, and two pieces of one slot never pair. The score is the rules' colour, formality, silhouette and pattern components;
  with a persona (`personaId`, else active, else first) the piece's match is mixed in, which reorders but never excludes.
- `outfitCount` is how many of the outfits behind `possibleOutfits` wear the item: the outfits of all tops add up to `possibleOutfits`. A layer
  or an accessory counts the core outfits it can be worn with.
- `unlock` is `{ slot, productId, name, categorySlug, color, imageUrl, lowestPrice, cheapestSeller, goPath, unlocksEstimate, matchScore? }`: one
  product for the emptiest of top, bottom, shoes and outerwear, for the slots with fewer than 3 companions. It pairs with the item, is in stock
  and in the persona's budget, not already owned, with a persona a `matchScore` of at least 60, and neutral when possible. `lowestPrice`,
  `cheapestSeller` and `goPath` are one offer, the cheapest by effective price (price plus shipping). `unlocksEstimate` is the outfits the item
  would gain. It is null when those slots all have 3 companions, or when nothing in the catalog fits.

**Storage** is any S3-compatible store (MinIO in compose). Keys are `wardrobe/{userId}/{itemId}/{original|cutout|thumb}.{ext}`; the bucket
policy (`docker/minio/public-read.json`) makes `cutout.*` and `thumb.*` publicly readable, originals are private. Settings:
`Storage:Endpoint` (what the API talks to), `Storage:PresignEndpoint` (what the app uploads to; defaults to `Endpoint`),
`Storage:PublicBaseUrl` (bucket included), `Storage:Bucket`, `Storage:AccessKey`, `Storage:SecretKey`, `Storage:ForcePathStyle`. In compose,
`STORAGE_PUBLIC_URL` (default `http://localhost:9000`) sets both public addresses; use `http://<your LAN IP>:9000` for a phone.
Vision: `Vision:BaseUrl` (compose: `http://vision:8090`; `http://localhost:8092` for `dotnet run`) and `Vision:InternalKey`
(`VISION_INTERNAL_KEY`, the same value as `kombin-vision/.env`).

| Job | Schedule | Does |
|---|---|---|
| `wardrobe-analyze` | per item, enqueued by `process` | the photo analysis above; 3 retries |
| `wardrobe-cleanup` | daily 05:00 `Europe/Istanbul` | deletes the images of items archived more than 30 days ago (the row and its wear history stay), and uploads started but never processed for a day (row and object) |

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
  copying images to our own storage, product embeddings (pgvector is installed; only wardrobe items have embeddings so far),
  TimescaleDB for `price_snapshots`.
- Engagement: rate limiting and bot filtering on `/go`, affiliate conversion/postback import, real affiliate templates per
  seller, forwarded-headers setup.
- Notification: the first run of `alert-dispatcher` on a database that already has unconsumed triggers turns every one of them
  into a notification (the oldest 500 per minute); mark old triggers consumed before enabling Hangfire there. The Hangfire
  dashboard has no authentication, which is why it exists in Development only.
