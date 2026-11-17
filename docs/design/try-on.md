# "Manken üstünde gör" (try-on), the backend

Task P3. The specification is the vision team's brief (`kombin-vision/docs/design/try-on-briefs.md`, "Backend brief") and the contracts of
`POST /v1/garment/prepare` (`prepare-v6`) and `POST /v1/tryon/render` (`mannequin-v5`) in `kombin-vision/README.md`. This note says how the
backend does it and where it differs from the brief. The feature is **"Manken üstünde gör"**, a catalogue drawing on a mannequin, never a
picture of the user.

## Slice and boundaries

A feature of its own, `Features/TryOn`, schema `tryon` (one schema per feature: the brief's `wardrobe.tryon_garments` and
`recommendation.tryon_renders` would have put one feature's rows into two other features' schemas). Entities in
`Persistence/Entities/TryOn`, configurations in `Persistence/Configurations/TryOn`.

| Reference | Kind | Why |
|---|---|---|
| TryOn → Profile `SizeHints`, `SizeHintResponse` | Seam | `referenceSize` is the size hint's own size, else its suggestion (the brief). The same seam Catalog uses for the product page. |
| TryOn → Recommendation `HowToWear`, `HowToWearAnchor`, `OutfitStore` | Seam | `?productId=…&withWardrobe=true` is "Dolabımla dene": the how-to-wear engine with a third caller, never a copy. |
| TryOn → Catalog `CatalogImageFetcher`, `ImageFetch`, `ImageFetchStatus` | Seam | Retailer images are downloaded only through the catalogue's polite, guarded fetcher (public addresses, `KombinBot`, one request per host per second). |

Nothing in another feature references TryOn: the job **pulls** (like `product-embeddings` and the league), so Wardrobe, Catalog and Profile
push nothing into it. That is why invalidation is by cache key (below) and not by calls from `UpdateProfile` or the size endpoints, which
would make TryOn and Profile a cycle.

The vision client moved from `Features/Wardrobe` to **`Common/Vision`** (the allow-list already said that is where it belongs): TryOn needs
it and a new `Legacy` line is not allowed. The move removed the vision-client `Legacy` references of Admin, Catalog, Lens and Social.

## Tables

`tryon.garments`, one row per owner (a wardrobe item **or** a product, check constraint), updated in place:

| column | |
|---|---|
| `id` | the garment ID reports point at |
| `wardrobe_item_id` / `product_id` | exactly one; FK with cascade to `wardrobe.items` / `catalog.products`; unique each |
| `status` | `Pending` (no answer yet: asked for, or one transient failure waiting for its retry), `Prepared` (the service answered), `Refused` (4xx, no usable image: permanent until the source changes), `Failed` (the second transient failure: retried after a day) |
| `source_hash` | SHA-256 of what was (or is to be) sent: the category slug plus the cutout key (wardrobe) or the first six image URLs in gallery order (product) |
| `prepare_version`, `family`, `source`, `reliable`, `may_be_outermost`, `sleeves` (jsonb), `flags` (text[]), `landmarks` (jsonb), `cutout_key`, `width`, `height`, `image_index`, `prepared_at` | the last answer, as the brief lists them; null until the first answer |
| `attempts`, `next_attempt_at`, `lease_until`, `requested_at`, `last_error` | the job's bookkeeping |

`tryon.renders`: `id`, `user_id` (cascade), `cache_key` (unique per user), `body_hash`, `renderer`, `image_key` **or** `refusal` (the
renderer's 422 code for exactly these inputs; check constraint), `width`, `height`, `garments` (jsonb: `[{ id, fitLevel }]`),
`defaulted_measurements` (text[]), `created_at`, `last_used_at`.

`tryon.reports`: `id`, `user_id` (cascade), `outfit_id` or `product_id`, `garment_ids` (uuid[], the `tryon.garments` rows), `renderer`,
`created_at`.

**Differs from the brief:** one garment row per owner instead of unique (owner, version). The attempt state (retry once, back-off) lives on
the row, and reports count per garment across versions. The cache key carries the row's version, `prepared_at` **and** the source hash it
answered, so an in-place re-prepare changes it like a new row would. A wardrobe item added from the catalogue (`FromCatalog`) uses **its product's** row: its stored
"cutout" is the product's primary image unchanged (often a model photo), while the product's row was chosen from all six images.

Prepared cutouts are stored as `tryon/{userId}/{garmentId}/prepared.png` (catalogue rows under the empty user ID), renders as
`tryon/{userId}/{renderId}/render.webp`. Neither name is `cutout.*` or `thumb.*`, so both are private: renders are served through a
presigned GET (1 h), cutouts are only ever read by the server.

## The job: `tryon-prepare`

Every minute, `[AutomaticRetry(Attempts = 0)]`, does nothing while `TryOn:Enabled` is false. Tests call `ProcessAsync(now, itemIds,
productIds, userIds, ct)` with their own IDs (the database is shared). One run:

1. **Renders:** deletes renders made for other measurements than the user's current ones (a profile updated after the render, other
   `body_hash`) and renders unused for `TryOn:RenderRetention` (30 days), rows and objects.
2. **Wardrobe items:** `Ready`, not archived, with a cutout and a category, **never one whose analysis had `person_wearing`** and never a
   `FromCatalog` item (it uses the product). Due when it has no row, its source hash changed, a `Pending` or `Failed` row's
   `next_attempt_at` passed, or (last, low priority) its answer is of another version than the current one. Sends the stored cutout (one
   PNG with alpha). Up to `TryOn:WardrobeBatch` per run; a few in parallel are fine (the brief), they run one after another here.
3. **Products:** active ones, plus inactive ones a wardrobe item came from or a request asked for, with at least one image, in a try-on
   category. Same due rule; order: asked for by a request, no row, source changed, retries, then the version re-prepare. Sends **all** images,
   up to 6, in gallery order (`sort_order`); the service picks and returns `imageIndex`. **Only under a PostgreSQL session advisory lock**
   (`TryOnCatalogueLock`, `pg_try_advisory_lock(0x4B4F4D42, 4)`), one product after another: at most one catalogue prepare at a time, whoever
   asks (the minute run or a request's fire-and-forget job). `TryOn:DayCatalogueBatch` (5) products per run during the day,
   `TryOn:NightCatalogueBatch` (40) between `TryOn:NightStartsAt` and `TryOn:NightEndsAt` (01:00-07:00 Istanbul), and every run stops after
   `TryOn:RunBudget` (50 s), so runs do not pile up.

"The current version" is the version of the most recent answer. Rows of another version are re-prepared in the low-priority tail of each
run; a top prepared before `prepare-v6` is refused on render (`422 garment_not_ready` → `Unavailable`) until then, as the brief says.

**No double prepare.** A prepare first **claims** its row (`UPDATE … SET lease_until = now + 5 min WHERE lease_until IS NULL OR lease_until <
now`, the row created with `ON CONFLICT DO NOTHING` when missing); a run or request that does not get the claim skips the owner.

**Failures** (`TryOnPrepareRules`, pure): `503 busy`, 5xx, 401/408/429 and network errors are transient: the first one sets
`next_attempt_at` to `TryOn:RetryAfter` (10 min) later (**one later retry**), the second makes the row `Failed` with a retry after
`TryOn:FailedRetryAfter` (24 h). Any other 4xx is `Refused` (never retried until the source changes). A row that already had an answer keeps
it through failures (status stays `Prepared`).

**Photo size.** The service refuses more than 8 MB per photo and 16 MB per request (`413`) without looking. Before sending,
`Common/Imaging/VisionImageFit` scales any photo over 8 MB to at most 1600 px on its long side (PNG when it has transparency, else JPEG 90),
then, while the request is over 16 MB, scales the largest remaining photos the same way; a photo that still does not fit is left out (never
the last one). Retailer images are fetched through the catalogue fetcher with a cap of `TryOn:MaxSourceImageBytes` (50 MB; Jack & Jones'
packshots are up to 45 MB), and a photo over `TryOn:MaxSourcePixels` (60 MP) is not decoded at all (a decompression bomb costs nothing).

**The image library: SkiaSharp** (`SkiaSharp` + `SkiaSharp.NativeAssets.Linux.NoDependencies`, 4.154.0). The API had none; scaling a PNG or
JPEG needs a decoder and an encoder, which is not something to write by hand. Licence: **MIT** (Skia itself BSD-3-Clause), which allows free
commercial closed-source use at any size, with no revenue threshold. The alternatives: ImageSharp is managed (no native code) but under the Six
Labors Split License, free only below USD 1M annual gross revenue, a trap for later; Magick.NET is Apache-2.0 but ships the whole ImageMagick.
SkiaSharp's `NoDependencies` assets include `linux-musl-x64`, so the Alpine runtime image needs no extra package. Cost: about 12 MB of native
library in the image; decoding is native code, which is why the pixel cap and the byte cap come first.

## Readiness (`TryOnReadiness`, pure)

The rule at the top of the brief, checked in this order:

1. **Body:** BMI over `TryOn:MaxBmi` (36) → `Unavailable` (`BodyOutOfRange`). BMI = weight / height², with the audience's default height
   (women 165, men 177, unisex 171: the service's `mannequin.py`) when there is none; **no weight = the audience's default BMI (22 / 24), inside.**
2. **Shape:** no top (top or outerwear; there is no dress category) → `NoTop`; no bottom (trousers or skirt) → `NoBottom`. "Dolabımla dene"
   with a product alone is therefore `Unavailable`: the figure is never drawn with bare legs.
3. **Core pieces** (top, outerwear, trousers, skirt, shoes if the outfit has them): a wardrobe piece with `person_wearing` (never prepared) →
   `PersonInPhoto`; a piece without a category → `UnknownCategory`; a row `Refused` / `Failed` or answered `reliable = false` →
   `PieceNotReady`. **Bags and belts never block** (never reliable; drawn beside the figure when they have a row, left out otherwise).
4. **Outermost upper garment** (the outerwear if any, else the top): answered `may_be_outermost = false` → `FoldedSleeves`. A refused top
   under outerwear is fine; outerwear is never restricted.
5. A core piece with no row or a `Pending` row → `Preparing` (the request creates the missing rows as `Pending`, marked as asked for, and
   enqueues one targeted prepare, at most once a minute per row).
6. Otherwise `Ready`.

Every `422` of `/v1/tryon/render` (`incomplete_outfit`, `garment_not_ready`, `folded_sleeves`, `body_out_of_range`, and any other 4xx) is
`Unavailable` with reason `RendererRefused` and is never retried: the refusal is stored under the cache key (`renders.refusal`, no image), so the
same inputs answer `Unavailable` without asking again; any change of the inputs is another key and another try.

## The endpoints

`GET /api/v1/me/tryon?outfitId=…` (one of the caller's outfits: daily, how-to-wear, saved; another user's is 404) or
`GET /api/v1/me/tryon?productId=…&withWardrobe=true` ("Dolabımla dene": the how-to-wear engine around the product, its best outfit made of
the caller's own pieces only; `withWardrobe=false` is the product alone, so `Unavailable`) →

```jsonc
{ "status": "Ready",            // Ready | Preparing | Unavailable
  "imageUrl": "https://…",      // Ready only: a presigned GET, 1 h
  "garments": [ { "id": "…", "fitLevel": "TrueToSize" } ],   // Ready only; id = the piece's ID as the outfit carries it (wardrobe item or product)
  "defaultedMeasurements": ["chest", "hip"],                 // Ready only: what the mannequin guessed
  "reason": null }              // Unavailable only: why (for the app team and support; the app hides the action whatever it says)
```

Render request: `body` from `UserProfile` (`women` / `men` / `unisex`, height, weight, chest, waist, hip; no shoulder measurement exists),
per garment `family, landmarks, reliable, source, flags` from the row, `cut` = the piece's `ProductFit` (none when `Unknown`), `size` = the
wardrobe item's size (**a catalogue piece has none**: the user has not chosen one), `referenceSize` = `SizeHints.BuildAsync`'s own size, else
its suggestion; the cutouts as `garment` files in the outfit's order; width `TryOn:RenderWidth` (600), WEBP. A transient render failure is
503 `TryOn.RendererUnavailable` (the app hides the action; a later request tries again).

`POST /api/v1/me/tryon/reports` `{ outfitId | productId, garmentIds, renderer }` ("Yanlış görünüyor") → 204. The IDs are the response's
garment IDs; each must be a piece of that outfit (or the product, or one of the caller's own wardrobe items for "Dolabımla dene") with a
garment row, else 400 `TryOn.UnknownGarment`. The same report again (user, subject, renderer) stores nothing new. No review screen yet:
`docs/metrics.sql`'s `-- tryon: tryon_reported_garments` lists reported garments by count.

## Cache key and invalidation

Key = SHA-256 of: the renderer (`/health`'s `renderer`, read once per process like the CLIP model), the body values (audience, height,
weight, chest, waist, hip), and per garment its row ID, version, `prepared_at`, the source hash it answered, size, reference size and cut, plus
the width. So:

- measurements or audience change → another key (and the run deletes the old renders within a minute: they show the old proportions);
- a size entry changes (`UpsertSize`, `ConfirmSize`, `DeleteSize`) → the reference size changes → another key;
- an item's size changes → another key;
- a product's images change → the row is re-prepared (`prepared_at`) → another key; the vision service's version too.

A cache hit is free (no rate limit), updates `last_used_at` and answers a new presigned URL.

## Limits

30 new renders per user per hour (`TryOn:RendersPerHour`; every call to the renderer counts, a `422` too, cache hits never) and one render per
user in flight (a second request while one renders answers `Preparing`). Both in `TryOnRenderGate`, in memory **per instance**, like the
rate limiter (a second API instance would double them). Over the hour: 429 `RateLimited` with `retryAfterSeconds` and `Retry-After`, the
rate limiter's shape.

## Account deletion and privacy

A render reveals body proportions. `AccountPurger` deletes the user's render images and the prepared cutouts of the user's wardrobe items
(objects first, as for every other object); the rows go with the cascade (`tryon.renders` and `tryon.reports` on the user,
`tryon.garments` on the wardrobe item). Catalogue rows are catalogue data, not user data. Body values never reach a log line or an analytics
event (the client logs a failed call's path and the service's error code only). `docs/privacy-notes.md` section 1a and 6,
`docs/security-review.md` "Try-on".

## Switch

`TryOn:Enabled` (env `TryOn__Enabled`), default **false**: the routes stay mapped (OpenAPI, the authorization matrix) and answer 404
`TryOn.Disabled` first; the job does nothing. The tests switch it on.

## Open questions

1. **The back view of tops.** About 8 % of chosen top/outerwear photos are the back; only reports find them. The brief says the backend can
   mark a reported garment unready after review: there is no review screen yet (the reports table and the metrics query are the start).
2. **Per-instance limits.** Fine with one API instance; a second one needs the limits in PostgreSQL.
3. **The catalogue at launch.** About 2,800 active products at ~2 s each and 40 per night-minute is about 1.5 hours of nights; the daytime trickle
   (5 a minute) keeps up with new products. A new vision version re-prepares everything the same way.
4. **Inactive products** stay prepared (their rows cascade only when the product is deleted, which never happens today); they cost a row and a
   cutout each.
5. **A catalogue piece's size** is unknown, so its fit label is the service's `null` (drawn true to size). Should "Dolabımla dene" assume the
   size hint's size (always "Tam bedenin")? Left to the app team.
