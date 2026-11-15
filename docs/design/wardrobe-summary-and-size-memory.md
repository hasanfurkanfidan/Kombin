# Wardrobe summary and size memory (Task P2)

Two small features that give the wardrobe and the size map something back to the user without asking for more input.

## What conflicted or was unclear in the brief

| Brief | What exists | Decision |
|---|---|---|
| "No query per piece" and, for "eşi yok", "the existing suggestion that would unlock it" | The unlock suggestion (`GetPairingsEndpoint.FindUnlockAsync`) is one catalog prefetch per piece. | Everything else is set-based. The unlock is looked up for the **first 3** pieces with the reason "eşi yok" only (`WardrobeSummaryRules.UnlockLookups`), so the cost is bounded (≤ 3 prefetches), never one per piece. The other pieces still say "eşi yok", with no `unlock`. |
| B1: "reuse the click, do not add a second table unless you must" | `engagement.clicks` is the append-only revenue record; one product can have many clicks; the answer (and "do not ask again") is mutable state. | A table is needed: `profile.size_prompts`, one row per (user, product). It is **not** written by `/go` (the revenue path is untouched): `GET /me/size-prompts` creates the rows from the user's clicks (the unique index makes parallel requests harmless), the way `GET /me/profile` creates the profile. |
| "Three to seven days later the app may ask" and "a question expires after 30 days" | - | A question is listed from 3 days after the click until it is answered or 30 days after the click. The 7 days is the time the app is expected to ask in, not a limit. |
| "Using the existing confidence rules" | `POST /me/sizes/confirm` already stores a confirmed fit (source `Confirmed`, confidence 1) and doubts (0.5) a stored size only when the size is the same. | The prompt answers apply exactly those rules (same constants), so the two routes never disagree. |
| "Rate limits like the neighbouring endpoints" | The wardrobe and size routes have no named policy, only the global fallback (600 per minute per address). | The new routes use the global fallback too; no new policy. |
| Price currency | `PurchasePrice` has no currency; offers are `TRY`. | Everything is `TRY` (`currency` is in the response). |

## A. `GET /api/v1/me/wardrobe/summary`

Pieces counted: the user's visible, **ready**, not archived pieces (unfinished uploads and failed items are not "in the wardrobe" yet).

**Small wardrobe.** Fewer than `MinPieces` (5) pieces: `{ pieceCount, message }` and nothing else (no totals, no zeros).

**Value.** Per piece, in this order: its own `purchasePrice` (a real price, counted as `priced`); else, when it is linked to a catalog product (`productId`), that product's lowest price today (the catalog list's `lowestPrice`: lowest active in-stock offer), counted as `estimated`; else `unpriced`, left out of every sum. A product nobody sells has no price, so its piece is unpriced. The three counts add up to `pieceCount`.
`total = knownTotal + estimatedTotal`; `isEstimate` is true when `estimatedTotal > 0`: the app labels the total (and any category total that has `isEstimate`) "tahmini". With nothing priced, `total` is null and `note` says so. `byCategory` groups by the top-level category (first slug segment), each with its own totals and counts; pieces without a category are one group with a null slug.

**Cost per wear.** The pieces with a price (own or estimated) and `wearCount ≥ 1`, most worn first (then latest worn, then ID), top 5: `price / wearCount`, rounded to 2 decimals, `priceIsEstimate` says which kind of price it is.

**Giymediklerin.** A piece is listed when all hold: created at least 30 days ago, `wearCount = 0` (no recorded wear), and in no outfit whose status is `Saved` or `Worn` (any kind, daily, how-to-wear or budget). Ordered oldest first, at most 20 returned, `totalCount` is the number of all. The likely reason, one per piece, first match wins:

1. `NoPartner` ("eşi yok"): the piece's `companionCount` is 0 (`OutfitCounts.CompanionCountsAsync`, the same number as the grid and `unmatchedCount`). For the first 3 such pieces the response carries `unlock`, the pairings route's own `PairingUnlock` (`FindUnlockAsync`), when the catalog has one.
2. `OutOfSeason` ("mevsimi değil"): the piece's `seasons` do not include the current season (`WeatherRules.SeasonOf` of today in Istanbul; a piece with no season set has no reason).
3. none.

No new matching rule, no new scoring: the pairing, unlock and season code is the engine's.

**Cost.** One request, a constant number of queries: the pieces (projection), the categories, the products' lowest prices (one grouped query), the outfit memberships of the candidates (one), the companion counts (cached under the wardrobe fingerprint), the persona and wardrobe for the unlock (only when some piece has "eşi yok"), and the item summaries (two). Budget: 300 ms p95 at 200 pieces / 50k products, in `OutfitPerformanceTests` (the same container, `KOMBIN_PERF=1`).

## B. Size memory

**Rows.** `profile.size_prompts` (cascade on the user and on the product): `user_id`, `product_id`, `seller_id`, `offer_id` and `click_id` (of the first click on that product in the window, no foreign keys: the revenue record already guards them), `clicked_at`, `answer`, `answered_size`, `answered_at`. Unique `(user_id, product_id)`: one question per product, ever (an answered or dismissed product is never asked again). Products in the accessories group (bags, belts; `SizeGroups` catch-all) get no question: there is no size to remember.

**List.** `GET /me/size-prompts` (when `sizePromptsEnabled` is true): first creates the missing rows (clicks of the user from the last 30 days, at least 3 days old, first click per product), then returns the unanswered, unexpired ones, newest click first, at most 3: `{ prompts: [{ id, product { id, name, imageUrl }, brand, seller { name, slug }, group, knownSize { size, source, confidence }?, availableSizes, clickedAt, expiresAt }] }`. `knownSize` is the size map's entry for that brand and group (so the app can pre-fill); `availableSizes` are the sizes the clicked offer listed.

**Answer.** `POST /me/size-prompts/{id}` with `{ answer, size? }`, `answer` one of `BoughtFits` (size required), `BoughtTooSmall`, `BoughtTooLarge`, `NotBought`, `NeverAsk` (do not ask again for this product). The answer is stored on the row (the question is gone from the list) and:

- `BoughtFits`: the size map entry for the product's brand and size group becomes that size, source `Confirmed`, confidence 1 (`POST /me/sizes/confirm`'s rule). A size the user typed is replaced only here, by a confirmed fit.
- `BoughtTooSmall` / `BoughtTooLarge`: never a new size, never a guess. If an entry exists and the answer names no size or the same size, its confidence drops to 0.5 (`DoubtedConfidence`); a different size, or no entry, changes nothing.
- `NotBought`, `NeverAsk`: no size change.

404 `Profile.SizePromptNotFound` for an unknown ID or someone else's; 409 `Profile.SizePromptExpired` after 30 days; answering again with the same answer and size is a 200 no-op, any other answer is 409 `Profile.SizePromptAnswered`.

**The switch.** `UserProfile.SizePromptsEnabled` (default true, `GET`/`PUT /me/profile` `sizePromptsEnabled`; `PUT` left out or null = unchanged, as for `audience`). Off: the list is empty and creates nothing. Questions already answered stay answered; turning it on again shows the still-valid ones.

**Privacy.** The rows are personal data (what a user bought, where, which size): they cascade with the user, are listed in `docs/privacy-notes.md` section 1a and in `AccountDeletionTests`' matrix. No push, no email.

## Analytics

`wardrobe_summary_view` (`pieceCount`, `unwornCount`, `hasEstimate`), `size_prompt_view` (`promptId`), `size_prompt_answer` (`promptId`, `answer`): IDs, counts and enum names only.
