# Outfit engine (Task 12)

## Approach

A **rule table + weighted score + diverse pick** pipeline, pure and in memory, fed by at most a handful of narrow queries.

1. **Load** (per request): the user's ready, categorised, non-archived wardrobe (≤ a few hundred rows, one query), the persona
   and its `PersonaStyleProfile` (the catalog's persona convention: `personaId`, else the active persona, else the first), the weather
   for the profile's `City` (cached per city and hour), and the outfit hashes the user dismissed in the last 30 days.
2. **Prune pieces.** Each piece gets a slot from its category (`ust-giyim/*` → Top, except `ust-giyim/blazer`, worn as a layer →
   Outerwear; `alt-giyim` → Bottom; `dis-giyim` → Outerwear; `ayakkabi` → Shoes; `aksesuar` → Accessory) and a piece score
   (`MatchScorer` for the persona, occasion band fit, season/weather fit, recently worn). Hard filters drop what can never work
   (avoided colours, banned by the weather, formality two steps outside the band). The best K per slot survive (10/10/6/5, plus 4
   catalog pieces per slot when gaps are allowed).
3. **Enumerate** Top × Bottom × Shoes × (Outerwear or none), rejecting on the hard rules early (they are monotone: a clashing pair
   cannot be rescued by a third piece), scoring the rest (a few thousand combinations at most, single-digit ms). The best matching accessory is added afterwards; it never decides an outfit.
4. **Pick diverse.** Sort by score; greedily keep outfits that differ from *every* kept one in at least two pieces (up to 12). For
   *daily*, `seed` (plus the day, so tomorrow does not repeat today) indexes into that list, so any two seeds that land on
   different entries differ in ≥ 2 pieces; the next two entries are the `alternatives`. For *how to wear*, a seeded, order-free
   jitter (FNV hash of seed + outfit) re-ranks near-equals, and selection caps every non-anchor piece at 2 of the 5 outfits.
5. **Gaps** (catalog pieces) are fetched only when needed: daily asks the catalog only when the wardrobe cannot produce a
   `Complete` outfit above the threshold (the honest commercial moment), and then fills exactly one slot; how-to-wear fills the
   slots the wardrobe cannot (around the anchor's colour partners and formality ± 1, ignoring the day). The prefetch is one narrow query per slot: leaf categories of the slot, colours that harmonise with
   the anchor/outfit (neutrals + same/analogous/complementary), formality band, active with a sellable offer, persona audience
   and budget, newest 30. The cheapest seller is chosen with `GetProductEndpoint.EffectivePrice` (price + shipping), the
   catalog's own ordering. A partial index `(category_id, primary_color, formality) WHERE is_active` keeps it narrow at 50k.
6. **Narrate.** `TemplateNarrator` writes the line synchronously (deterministic per outfit). When `Narration:Provider = Anthropic`,
   a cached LLM line is used if one exists; otherwise the response carries the template line and an `outfit-narrate` Hangfire job
   upgrades it in the background (one call per outfit set, cached by narration hash for 7 days in `recommendation.narrations`).

## The scoring (`OutfitScoring.cs`, rules in `OutfitRules.cs`)

`score = 100 × (0.25 colour + 0.15 formality + 0.10 silhouette + 0.10 pattern + 0.10 weather + 0.10 occasion + 0.20 persona) − penalties`

- **Colour** (`ColorRules`): neutrals (`GapAnalysis.Neutrals`, plus blue/navy denim) go with anything. Count the distinct
  non-neutral colours: 0 = calm all-neutral (0.85), 1 = "one statement piece" (1.0), 2 = only as a listed pair (monochrome
  tonal, analogous, complementary; table), 3+ = reject. `Multi` is always a statement. Black + navy and black + brown are
  allowed but quieter (0.7).
- **Formality**: core pieces spread ≤ 1 (0 → 1.0, 1 → 0.85); outerwear may sit one above the core's maximum.
- **Silhouette** (`SilhouetteRules`, top fit × bottom fit table): oversized top wants slim/regular bottom; oversized on oversized is
  rejected.
- **Pattern** (`PatternRules`): at most one strong pattern (striped, checked, printed); two only when both are small-scale (we have
  no scale attribute, so the table names the small-scale cases: a striped/checked shirt, any accessory) and not two prints.
- **Weather** (`WeatherBands`): five temperature bands from the day's `(2·max + min) / 3` → outerwear forbidden / optional /
  required and banned leaves (no boots or knitwear when hot; light layers only when warm). Rain → outerwear required, boots
  preferred, white shoes and loafers penalised, open shoes banned (none in the catalog yet). No weather → the date's season
  against each piece's `Seasons`.
- **Occasion** (`Occasions`): `office 3–4, weekend 1–3, evening 3–5, sport 1–2 (sneakers), event 4–5` plus family weights; the
  outfit's family is the most common `ProductFamilies.Derive` of its pieces. No occasion → the persona's range and `FamilyWeights`.
- **Persona**: mean `MatchScorer` score of the pieces (so swipes shape the ranking).
- **Penalties**: −5 per wardrobe piece worn in the last 2 days, −3 per catalog gap (prefer what you own).

Acceptance threshold **60**. `possibleOutfits` uses a **cheaper counting path**: core triples only (top × bottom × shoes), only the
hard rules and the persona-independent components (colour, formality, silhouette, pattern, renormalised), pairs pre-pruned, capped
at 999, cached in memory per user under a wardrobe fingerprint (count + latest `updated_at`), so any wardrobe change invalidates it.
`unlocksEstimate` is the same counter run with the gap's hypothetical piece added (solid, regular fit, the gap's colour and
formality); for outerwear it counts the core outfits it can be layered over.

## Data (schema `recommendation`)

`outfits` (user, persona, kind `Daily|HowToWear|Shopping`, completeness `Complete|NeedsOne|CatalogOnly`, occasion, for-date, weather
snapshot jsonb, seed, score, rule, hash, status `Suggested|Saved|Worn|Dismissed`, saved/worn/dismissed timestamps, dismiss reason,
narration text + provider + narration hash); `outfit_items` (slot, role `Anchor|Wardrobe|Gap`, wardrobe item or product + offer,
price at suggestion time); `outfit_feedback` (append-only: action `Save|Dismiss|Worn|Shuffle`, reason when dismissing);
`narrations` (narration hash → text, model, created). `UserProfile.City` (≤ 80). Repeating a `GET /daily` with the same inputs reuses
the suggested row with the same hash instead of inserting a new one. `worn` goes through the Wardrobe wear logic
(`WearSummaries.RecordAsync`, extracted from `RecordWear`), stamping `OutfitId` on the wear rows.

## Trade-offs considered and rejected

- **Random sampling / genetic search**: non-deterministic or hard to explain; exhaustive enumeration over pruned slots is cheap at
  these sizes and a stylist can read why an outfit won.
- **Scoring catalog products in bulk** (the 500-candidate pattern of `sort=match`): would break 300 ms at 50k; the prefetch is narrow
  by construction and only runs for slots that need it.
- **Pushing rules into SQL**: same reason as `MatchScorer`; rules must stay readable and unit-testable.
- **Embeddings (pgvector) for compatibility**: no training data, no labels; v2.
- **Calling the LLM in the request path**: latency and cost; templates first, background upgrade, cache by hash.
- **Persisting the outfit count**: an in-memory cache keyed by a fingerprint needs no invalidation hooks in the Wardrobe write paths.

## Deliberately left for v2

Learning from feedback (dismiss reasons re-weighting colours/formality, `DontOwnAnymore` archiving the piece), pattern scale and
material from the vision service, layering more than one top (shirt under knit), dresses/sandals (no categories yet), body-shape and
size-aware suggestions, a distributed cache for counts and weather, analytics events for outfits, and the Task 14 social layer on
saved outfits.
