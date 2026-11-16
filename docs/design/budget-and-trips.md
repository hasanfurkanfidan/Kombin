# Budget outfits and trip packing (Task P1)

Two features on top of the outfit engine (`outfit-engine.md`). Neither has rules of its own about what looks good: every outfit is scored
by `OutfitScoring.Evaluate`, enumerated by `OutfitGenerator.Generate`, kept only at or above `AcceptanceThreshold` (60), labelled
`Complete / NeedsOne / CatalogOnly`, hashed by `OutfitHash` and narrated by `TemplateNarrator` (plus a cached LLM line). What is new is
*which pieces the engine sees* and *how the results are picked*.

## 1. "Bütçeye göre kombin": a knapsack with a floor

**Problem.** Pieces `p` in slots (top, bottom, shoes, optional layer), each with a cost `c(p)` (0 when owned, the effective price of the
cheapest in-size offer otherwise). Find up to `count` outfits `O` maximising `score(O)` subject to `Σ c(p) ≤ budget`, `score(O) ≥ 60`, at
least one bought piece (otherwise it is the daily outfit, which exists), and pairwise diversity: two outfits share at most half of
either's bought pieces (`2·|B₁∩B₂| ≤ min(|B₁|,|B₂|)`). Ranking: score, then more owned pieces, then lower price, then hash. The engine's
−3 per catalog piece already prefers what the user owns.

**Algorithm.** (1) *Candidates, in SQL* (`BudgetCandidates`): per slot, active products of the slot's categories, the audience, the
formality band, no avoided colour, not owned, with an in-stock offer whose effective price is ≤ `budget × 1.5`. They are split into
three equal price bands of that cap and the newest 100 of each band are kept (≤ 300 per slot, so the knapsack sees cheap *and* premium
pieces). Backed by two partial indexes: `products (category_id, audience, formality) WHERE is_active` and
`offers (product_id, current_price) WHERE is_active AND in stock`. (2) *Offers in the caller's size*: the size map's entry for the
product's brand and group, else the inferred size (`SizeInference`, two agreeing brands); the cheapest offer listing it wins; offers
listing sizes without it are out; with no size known, or only offers that list no sizes, the cheapest offer is used and the piece is
`sizeUnknown`. (3) *Rerank* (`BudgetSearch.Shortlist`): pieces failing the day's per-piece rules (weather, band, avoided colours) are
dropped; the rest are cut into price quartiles and the best 3 per quartile by the engine's piece score survive (12 per slot, 8 layers),
the owned pieces are pruned exactly as daily prunes them. (4) *Enumerate* with `Generate`, which got two optional knobs: how many catalog
pieces per slot to keep, and a cost cap that rejects early (costs are non-negative, so the cap is monotone like the hard rules). Worst
case 22 × 22 × 18 × 14 combinations, in practice a few thousand evaluations after the pair rejections. (5) *Pick*
(`BudgetSearch.Pick`): sort, keep a diverse list of up to 12 greedily, and `seed` selects the window `[seed·count, seed·count+count)`
(mod length), so "başka öneriler" is the next seed and always fits the budget.

**Without shoes.** Shoes are the dearest slot (in the dev catalog every pair costs over 3,000 ₺), so a low budget must not dead-end.
When no outfit with shoes fits, the search runs again in a `WithoutShoes` context: the engine judges a top and a bottom (plus the layer
the weather requires) with every rule and the floor unchanged; only the occasion's required shoe has nothing to judge. Those outfits come
back with `withoutShoes: true` and "Bu bütçe ayakkabıya yetmedi; ayakkabısız bir kombin hazırladık." A signed-in user who owns shoes and
uses the wardrobe never gets this: their shoes cost 0, so the first search already answers. `includeShoes: false` ("Ayakkabım var")
skips shoes from the start (no shoe query, no message).

**Over budget.** Only when even the outfits without shoes do not fit: if an acceptable outfit with shoes costs at most 1.5 × budget, the
cheapest one (higher score on ties) is returned alone with `overBudgetBy` and the message "Bu bütçeyle tam bir kombin çıkmadı; en yakını
X ₺." Never more than one, never below the floor. Prices are per piece (price plus that seller's shipping), so two pieces from one shop can only cost less than shown.

**Limits.** The shortlist is a heuristic: an outfit made of a quartile's fourth-best piece is never seen. Prices move after the answer
(the stored item keeps the price at suggestion time, the `/go` page shows today's). The persona's own budget band is ignored: the request
says what to spend. Anonymous callers get no wardrobe, no size map, no weather (the season decides) and nothing is stored.

## 2. "Valiz hazırlama": a minimum-union selection

**Problem.** Days `d₁…dₙ` (n ≤ 14), each with a context (weather, occasion). For each day the engine yields acceptable wardrobe-only
outfits `O_d`. Choose one `o_d ∈ O_d` per day minimising `|⋃ o_d|` (core pieces; accessories are not packed), subject to: consecutive
days never wear the same outfit; tops at most twice per trip and never two days running (with `laundry`: no cap, still not two days
running); bottoms at most 3 times (no cap with `laundry`); shoes never two days running when `allowRepeatShoes` is false; layers free;
the union at most `maxPieces`. Ties: higher total score. This contains set cover (each day is an element, each piece covers the days it
can serve), so it is NP-hard; n and the wardrobe are small but not small enough to enumerate subsets of 150 pieces.

**Algorithm** (`TripPlanner`, pure, deterministic, every loop bounded). (1) *Pools*: per day, the pieces passing that day's rules,
ranked by the engine's piece score + 0.3 × versatility (the piece's `OutfitPairs` companions / the wardrobe maximum) + 0.2 × the share
of trip days the piece can serve; the generator's 10/10/6/5 per slot are kept, so pieces that work everywhere fill every day's pool.
(2) `O_d = Generate(...)`, capped at 200 per day. (3) *Greedy*: the most constrained day first (fewest outfits), each day takes the
outfit adding the fewest new pieces, then the higher score. (4) *Swap* (≤ 4 rounds): for each packed piece, fewest uses first, try to
re-dress every day wearing it from the remaining set; success removes it. Then each day is re-dressed with the best-scoring outfit
inside the set. (5) *Repair*: if a day is still bare although every day has outfits (an early choice used up what a later day
needed), a depth-first search in date order, packed pieces first, at most 20,000 steps, looks for a plan that dresses every day, and
the swaps run again on it. (6) Over `maxPieces`, the day with the most pieces of its own is dropped until the set fits. A day without
an outfit is counted in `coverage` and explained in `gaps`. Measured: about 1.4 ms per plan for 7-13 pieces (Debug, options and
search); end to end, a 14-day trip on a 150-piece wardrobe answered at p95 380 ms against the 800 ms budget
(`BudgetAndTripPerformanceTests`, 2026-11-15, a development laptop; budget outfits p95 301 ms signed in, 99 ms anonymous, against 600 ms).
A second run, right after the other latency classes on the same machine, measured trip 434 ms, budget 574 ms signed in and 200 ms
anonymous: the signed-in budget has the thinnest margin (enumerating 200 owned pieces with the catalog shortlist), so it is the first
place to look (smaller owned pools or a tighter shortlist) if CI's quiet runner disagrees.

**Distance from optimal.** On 300 generated wardrobes of 7-13 pieces and 3-5 days (seeded, some rainy days, random laundry and shoe
options; `TripPlannerTests`), compared with an exact search over subsets of the pieces: 244 instances could be decided and dressed;
the greedy plan was **optimal in 241 (98.8 %) and one piece above it in 3, never more** (mean excess 0.012 pieces), and it dressed
every day whenever the optimum did. The losses come from greedy order: an early day takes a locally cheap outfit whose pieces later
days cannot reuse, and the single-piece elimination cannot undo two pieces at once. Pools of 10/10/6/5 also hide pieces that only a
minimal set would need on big wardrobes, which the test cannot see (exact search does not scale there); the versatility and coverage
terms make that rare, not impossible.

**Weather.** `IWeatherProvider.GetDaysAsync`: Open-Meteo's forecast for days within its 16-day horizon (one call for the range),
climate normals beyond (the same calendar days of the previous three years from the archive API, averaged; rain when the mean is
≥ 1 mm or most years rained), the season when neither exists. Each day says `forecast`, `normal` or `season`; the warmth and rain rules
are the daily engine's `WeatherRules`.

**Gaps and thin wardrobes.** For a day without an outfit every core slot (and the layer when the weather requires one) with no
usable piece is a gap: `Missing` (none in that slot), `Weather` (the weather bans them), `Occasion` (all outside the band); with
pieces in every slot it is `NoCombination` (nothing scores 60) or `Repetition` (the wear rules ran out; tops first). One gap per
(slot, reason) lists its days and suggests one catalog piece from the narrow prefetch (neutrals, the band, the audience, the persona
budget) with its cheapest offer and `goPath …?src=trip`. With no covered day the answer says "Dolabında bu gezi için yeterli parça yok."
Nothing is invented: pieces come only from the wardrobe.

## Responses

- `POST /me/outfits/budget`, `POST /outfits/budget` → `{ budget, date, occasion, audience, weather?, overBudget, message?, outfits: [{ id?,
  status?, completeness, score, rule, pieces: [{ slot, role, source, wardrobeItemId?, productId?, name, categorySlug, color, imageUrl,
  price, offer?: { offerId, seller, price, originalPrice?, goPath, shippingCost }, size?, sizeUnknown? }], totalPrice, remainingBudget, ownedCount,
  savings?, overBudgetBy?, withoutShoes, narration }], withoutShoes }`; the body takes `includeShoes` (default true). Signed-in outfits are stored (kind `Budget`), so save, worn, feedback and dismiss work on them. `offer.price` includes the seller's shipping (the budget counts it); `offer.shippingCost` is that shipping (0 when free, for "kargo dahil") and `offer.originalPrice` is the crossed-out price on the same basis, list price plus the same shipping, so it is never below `price`. `savings` stays what the seller shows (list price minus price, both without shipping).
- `POST /me/trips/plan` → `{ city, startDate, endDate, pieceCount, pieces: [{ slot, items: [{ wardrobeItemId, name, categorySlug, color,
  imageUrl, days }] }], days: [{ date, occasion?, weather: { source, … }, outfit? }], coverage: { covered, total, percent }, gaps, message? }`.
- `POST /me/trips` (name + the plan request; the server plans again and stores its own result, never a client snapshot), `GET /me/trips`,
  `GET /me/trips/{id}`, `DELETE /me/trips/{id}`, `POST /me/trips/{id}/replan` (from today when the trip has started; 409 when it is over).

## Storage and caching

Schema `recommendation`, table `trips` (the outfit engine owns the feature): name, city, dates, the request and the plan snapshot as
jsonb (wardrobe item IDs, scores, gap offers, weather per day; names and images are read live, a piece removed since is `missing`),
counts for the list; cascade on the user; at most 50 per user. Cached in memory: companion counts (existing fingerprint cache), forecast
ranges per city and hour, normals per city and window for 7 days, geocoding per day. Nothing is cached per budget request: the catalog
moves.

## Left for v2

A real knapsack over all candidates (branch and bound on score per lira), combined shipping per seller, the cheaper size-specific price
in the SQL, budget outfits around an anchor, trips with several cities, laundry days chosen by the user, packing by volume or weight,
accessories and toiletries, buying the trip's gaps as one cart, and learning from which plans were saved.
