# Visual search: "Görselden ürün bul" (Task 15)

**The promise:** a photo of a garment (street, screen, hanger, gallery) shows the garments Kombin sees and, for each, the closest catalog
products with their cheapest seller, plus "dolabında benzeri var". It must feel instant and **never be embarrassing**: no sneakers for a coat,
and "Sonuç bulunamadı" rather than a wrong answer. Everything is a `/go`. ViT-B/32 stays; no training, text search, multi-photo or video.

## Embedding pipeline

| Space | Vector | Where | Computed by |
|---|---|---|---|
| Catalog | CLIP of the primary image (else the first), **as it is** | `catalog.product_embeddings` (1 row/product: vector(512), model, source image id + URL, status `Ready\|Unusable\|Failed`, attempts, next attempt, checked at) | `product-embeddings` job → `kombin-vision /v1/embed?removeBackground=false` |
| Query regions | CLIP of each detected region's cutout flattened on white; the whole photo when nothing is detected | memory only | `/v1/detect?embed=true` (new flag, task-15-embed): the crop embeddings already exist inside detect, so **zero extra model cost** |
| Wardrobe | CLIP of the cutout on white (existing, `/analyze`); catalog-added pieces use their product's vector | `wardrobe.items.embedding` | unchanged |

**`/analyze` vs `/embed`, measured** on 148 real catalog images of the local stack (Jack & Jones, Koton, Derimod; 4 torch threads, warm):
`/analyze` 3.0 s median, `/embed?removeBackground=true` 2.4 s, **`/embed` without rembg 0.39 s**. Catalog images are clean studio shots; rembg
costs 85 % of the time and buys nothing, so the job uses plain `/embed`. Region crops are cutouts on white and studio shots are on white/grey,
so the two live in comparable space (the calibration below is exactly that comparison).

**The job** (`ProductEmbeddingJob`, nightly 03:00 Istanbul + `POST /internal/catalog/embeddings/run`): loads every active product with an
image and its row's state in one query, keeps the due ones (`ProductEmbeddingRules.IsDue`: no row; new URL or new model; `Failed` after its
back-off 1 h → 24 h; `Unusable` only for a new URL or after 30 days, so a 404 is not refetched every night), new products first, **hosts
interleaved**. Per product: reuse the vector of another product with the same image URL (no download), else download politely
(`CatalogImageFetcher`: `KombinBot/0.1 (+https://kombin.app/bot; contact: …)`, 1 request/host/s via the pure `PoliteSchedule`, `Retry-After`
honoured, ≤ 8 MB, image types only) and embed; commit; clear the tracker. Resumable (state is in the rows), idempotent (a rerun finds nothing
due), one vision call at a time (the second vision slot stays free for wardrobe photos), stops after 3 vision failures in a row, 3 h budget.

## Index

pgvector 0.8.7 **HNSW, `vector_cosine_ops`, `m = 16`, `ef_construction = 64`**, created by the EF migration from the model
(`ProductEmbeddingConfiguration`). Why: HNSW needs no training and takes inserts as products arrive (IVFFlat needs a representative sample
before building and degrades as the catalog shifts); at 50k × 512 the graph is ~110 MB plus the 100 MB heap and builds in 15-30 s (single
worker, test container). `m = 16` is the usual sweet spot for ≤ 1M vectors of this dimension; a larger `m` buys recall we do not need for memory
and build time. `ef_search = 100` (default 40) keeps the top 48 effectively exact for a few ms more. Each search runs in a transaction with
`SET LOCAL hnsw.ef_search = 100` and **`hnsw.iterative_scan = relaxed_order`**: filters are applied after the graph walk, and iterative scanning
keeps walking until 48 rows pass, so a filtered search never starves (the relaxed order is irrelevant, results are re-ranked in memory). The
family filter joins `products.category_id = ANY(family ids)`: nothing denormalized that could go stale when a product changes category.
**Measured plans** (`LensPerformanceTests`, 50k): the whole-photo search walks the HNSW index (2 ms); a family search, where the filter
leaves ~6.5k rows, is planned as an exact scan of those rows (52 ms, perfect recall), which Postgres rightly finds cheaper at this size. That
exact path grows with the family: past ~200k products, denormalize the family onto `product_embeddings` with one partial HNSW index per family
(v2).

## Query flow

`POST /api/v1/lens/search?audience=` — **multipart** (`image`, ≤ 8 MB, jpeg/png/webp/heic), not a presigned upload: the photo must not be
stored, and a presigned flow exists only to store. The form is buffered in memory (endpoint form limits; ASP.NET would otherwise spill
uploads over 64 KB to a temp file) and the body is capped at 8 MB + 64 KB. Anonymous works; a bearer token adds wardrobe matches and the
profile audience.

1. Validate → read into memory → `/v1/detect?embed=true` (≤ 6 regions with box, slot, category + candidates, colour, embedding).
2. No region with an embedding → one whole-photo region (no category, all families, stricter floor).
3. Audience: `audience` param (`all` drops it), else the profile's Women/Men; Women/Men let Unisex through.
4. One transaction: per region, 48 nearest of the current vision model within its families (+ up to 3 nearest wardrobe pieces if signed in).
5. Cheapest sellable offers of all candidates (`OutfitSources.CheapestOffersAsync`) → `LensRanking.Rank` (floor, order, dedupe, 12).
6. Product list items (`ProductSummaries`), seller, `goPath = /api/v1/go/{offerId}?src=lens`; regions with results first, best first.

The photo is dropped when the request ends. **"Dolabıma ekle"** = the app crops the region on the device (box fractions) and runs the
existing wardrobe upload flow with the crop: no new backend path, nothing stored before the user asks.

## Quality guardrails

**Family:** `Top` (ust-giyim except blazer), `Outerwear` (dis-giyim + blazer, as `OutfitSlots`), `Bottom`, `Shoes`, `Bag` (canta), `Accessory`.
A region searches its family only; below confidence 0.35 it is widened to the families of its three candidates (never dropped).

**Calibration** (`LensRanking`): each of the 148 products' *second* image (mostly a model shot or another angle, a stand-in for a user photo)
went through `detect?embed`, and the region of the product's family was compared with all 148 primary-image vectors.

| Cosine similarity | p10 | p25 | p50 | p75 | p90 |
|---|---|---|---|---|---|
| region vs **its own product** | 0.57 | 0.61 | 0.68 | 0.74 | 0.77 |
| region vs best *other* product of the family | 0.55 | 0.59 | 0.64 | 0.68 | 0.71 |
| region vs best product of *another* family | 0.47 | 0.52 | 0.57 | 0.61 | 0.64 |
| whole photo vs own product / best product of another family | 0.72 / 0.55 | 0.78 / 0.61 | 0.83 / 0.68 | 0.88 / 0.72 | 0.92 / 0.76 |
| region vs the same garment as a wardrobe cutout / best other piece of its category | 0.65 / 0.63 | 0.71 / 0.67 | 0.79 / 0.72 | 0.85 / 0.77 | 0.89 / 0.81 |

Own product ranks 1st in its family in 77 % of photos, top 3 in 91 %, top 12 in 100 %. End to end through `/lens/search` (compose, 200
active products, 26 photos, two per category): the product itself first 13 times, in the rail 3, other products only 1, an honest empty
rail 9 (belts, and photos whose best match stays under the floor); never a product of another family. 133/148 photos had a region of the product's
family; 10 of the 15 misses were belts (no accessory band on a person). Numbers:

- **Floor 0.62** (regions): keeps the product itself in 74 % of photos; an unrelated product of another family passes in 20 %, and the family
  filter removes those. Below the floor the rail is empty and the app says "Bu parçaya yakın bir ürün bulamadık".
- **Whole-photo floor 0.78**: no family filter protects it, and a whole photo shares its background with studio shots (inflating every score), so
  it must clear the other families: the product itself is kept in 72 % of photos, a product of another family passes in 4 %.
- **`close` from 0.72**: above the best other product of the family in 93 % of photos, so "yakın eşleşme" almost always is the product.
- **Wardrobe** from 0.75 (own garment 65 %, best other piece 35 %); **`exact` from 0.88** (best other piece > 0.88 in < 5 %) or when the
  piece came from a product in the results.

**Dedupe:** same image URL (host case-insensitive) or the very same vector (identical distance: same pixels under another URL) → one result,
the first in order. **Order:** similarity − 0.05 for a product nobody sells, then price, then ID. **Audience** as above, always droppable.

## Cost model

- **Backfill, 50k products, 4 GB vision box:** 0.39 s of vision per product → **5.4 h of one vision slot**. Downloads are 1/s per host and
  interleaved. Measured on the compose stack: 200 products from 4 image hosts in 92 s (**0.46 s each**: 194 embedded, 6 Derimod images over
  8 MB marked Unusable) → **~6.5 h wall time for 50k**, i.e. three nightly 3 h runs or the run endpoint, resumable. One dominant host caps it
  at 1 product/s (~14 h). Afterwards a day's new products take minutes.
- **Memory:** idle 1.93 GiB, during the embed loop 1.93 GiB (measured with `docker stats`): `/embed` without rembg adds nothing measurable,
  unlike `/analyze` (+600 MB per call). The job leaves the 4 GB box's headroom to wardrobe analyses (peak 2.9 GB with both slots).
- **API:** one image (≤ 8 MB) in memory at a time; Postgres ~210 MB for 50k (heap + HNSW).
- **Query:** vision `detect?embed` 1.85 s median / 2.2 s p90 on this box (the dominant cost); our side **p95 71 ms signed in, 54 ms
  anonymous** (p50 56 / 48 ms; `LensPerformanceTests`, 50k products, one region, budget 400 ms p95).

## Left for v2

- **A fashion-specific model** (e.g. Marqo-FashionSigLIP or FashionCLIP, same 512-d family) once the catalog passes ~20k products or when
  own-product top-1 falls under ~80 %: ViT-B/32 barely separates the product from its family neighbours (0.68 vs 0.64 medians), which is what
  a fashion model fixes. The `model` column makes the switch a re-embed, not a migration.
- Per-family partial HNSW indexes once family searches stop being cheap as exact scans (see Index).
- Accessory regions on a person (belts, bags), separate crops for a top and the layer over it, all images per product (max similarity),
  colour-aware re-ranking, a perceptual hash for cross-URL duplicates, caching identical query photos.
- **Rate limiting** before launch: an anonymous endpoint in front of a 2 s CPU call is the cheapest DoS we own (out of scope here).
- robots.txt for image hosts (the workers check it for pages; images are fetched from URLs those pages gave us).
