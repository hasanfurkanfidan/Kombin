# Bulk add ("Toplu ekle"), the backend

Task BA1 (2026-11-18), design only. The specification is the vision team's brief (`kombin-vision/docs/design/bulk-add-briefs.md`, contract
`POST /v1/garment/bulk`; treated as fixed) and the single add in `Features/Wardrobe`. One photo of several garments laid out → a review
screen → the ticked pieces become ordinary wardrobe items. A few taps; nothing enters the wardrobe unseen. The brief's stop rule is
**provisional** (22 % wrong pre-ticks on the synthetic set), so the feature ships with the flag off and the numbers below are measured, not assumed.

## What stays as decided

Flag `BulkAdd:Enabled` (default false, routes answer 404 `Wardrobe.BulkDisabled`, like League and TryOn); the call to vision runs in a job and the
app polls; a bulk piece is the same record as a single one (same attributes, embedding, moderation, keys, Cache-Control, limits); staging is private
and deleted on confirm, cancel and after 24 h; a bulk confirm earns the piece points once; per-user daily limit and at most 12 pieces; account
deletion removes everything.

**One objection** (decision 4): delete the room photo as soon as vision has answered, not at confirm. After `Ready` or `Refused` nothing reads it
(thumbnails and cut-outs came back from vision; the user's phone still has the photo). It stays only while the batch is `Failed`, so "tekrar dene"
needs no second upload, and is deleted at cancel or after 24 h. The photo shows a bedroom, so seconds instead of minutes. Question 1.

## Slice and boundaries

No new feature. Bulk add is one more way to create a wardrobe item, so it is use cases inside `Features/Wardrobe` (`BulkCreateUploadUrl`,
`BulkProcess`, `BulkGet`, `BulkConfirm`, `BulkCancel`, shared files in `Features/Wardrobe/BulkAdd/`, jobs in `Wardrobe/Jobs`), tables in the
`wardrobe` schema, entities in `Persistence/Entities/Wardrobe`. A separate `Features/BulkAdd` would have to name `WardrobeModerator`,
`WardrobeAnalysis`, `WardrobeItemResponses` and `WardrobeLimits` (four `Seam` lines for one team's code) and gains nothing.

| Reference | Needed? |
|---|---|
| Wardrobe → Catalog (`EnumNames`, category tables) | Already on the allow-list for the same types (`WardrobeAnalysis`); no new line |
| League | None: it counts `wardrobe.items` rows (`DayCloser`); bulk writes ordinary rows |
| Identity `AccountPurger` | Entities and `Common/Storage` only (a prefix delete); no feature type |
| Analytics | None: the app sends events; the catalog lists them |
| Anything → BulkAdd | None: nothing outside Wardrobe may name these types |

**Expected allow-list change: none** (no `Seam`, no `Legacy`). If `NoFeatureReachesIntoAnotherOneBeyondTheAllowList` names a new type during the
work, it is moved to `Common` first (rule 1), never added as a line.

## a. Routes and shapes

All under `/api/v1/me/wardrobe/bulk`, `RequireAuthorization`, behind `FeatureEnabledFilter<BulkAddOptions>`. The upload is the single add's flow (presigned PUT, then
`process`): the client code and the 8 MB rule are known, the API never holds the photo in a request, and the job needs the object in storage anyway.

| Route | Does |
|---|---|
| `POST /upload-url { contentType, sizeBytes }` | creates a `Created` batch; 201 `{ batchId, uploadUrl, headers, expiresAt }`. Same validator and content types (jpeg, png, webp, heic, ≤ 8 MB). Policy `uploads` (60 per hour, shared with the single add: no new limiter). Checks the open-batch limit |
| `POST /{id}/process` | checks the object (missing 409 `Wardrobe.UploadMissing`, over 8 MB 400 `Wardrobe.ImageTooLarge`, object deleted), checks the daily limit, enqueues `bulk-add-analyze`; 202 `{ batchId, status }`. Idempotent like `items/{id}/process`: `Processing` and requested within 10 minutes → nothing new; `Failed` → starts again from scratch; `Ready`, `Refused` → returned as they are |
| `GET /{id}` | the status answer (d). Cheap; the app polls every ~1 s and again to refresh expired thumbnail links |
| `POST /{id}/confirm { pieces: [{ id, categorySlug?, primaryColor?, secondaryColor?, clear? }] }` | adds the listed pieces. Ticks are the list: a piece not in it is not added. 1 to 12 entries, no duplicates, every ID a piece of this batch (400 `Wardrobe.BulkUnknownPiece`). The edits are `PATCH /items/{id}`'s own fields and rules (same slug check `Wardrobe.UnknownCategory`, `clear: ["secondaryColor"]`); a new category recomputes formality and seasons exactly as `UpdateItem` does. Everything else is edited afterwards with `PATCH`, as today |
| `DELETE /{id}` | cancel; 204. Cancelled or expired → 204 again; confirmed → 409 `Wardrobe.BulkAlreadyConfirmed` |

**Confirm is idempotent by primary key.** A bulk piece's ID **is** the wardrobe item's ID (`wardrobe.items.id = bulk_pieces.id`), so the same piece can
never become two items, a retry after a timeout writes the same keys, and a half-failed attempt (storage down after some copies) is deleted and tried
again. The handler runs in one transaction that locks the batch row `FOR UPDATE`: a double tap waits, then sees `Confirmed`. A confirm of a `Confirmed`
batch answers **200 with the stored result** (`confirmed_item_ids`, in reading order; the new body is ignored: a screen cannot confirm twice). Confirm
and cancel race on the same lock: first wins, the other is 409. **Pieces not ticked are dropped with the batch**: one confirm per photo, no "add the
rest later" (the user takes another photo; the rows and objects of the rest are gone in the same request). The confirmed item gets `ConfirmedAt = now`
(the user looked at it: the single add's "one tap" already happened) and `Status = Ready`.

## b. Tables: staging area or items in a hidden status

| | New status `Staged` on `wardrobe.items` | New tables `bulk_batches` and `bulk_pieces` |
|---|---|---|
| Leak risk | Every query on `db.WardrobeItems` (grid, stats, outfit engine, gaps, lens matches, league, try-on prepare, summary, moderation retry, admin counts) must learn to skip it; one forgotten filter shows or scores an unconfirmed piece, or hands it to a stranger | Impossible by construction: nothing else reads these tables |
| Copying | None, but the object keys would have to be the final public `cutout.*`/`thumb.*` names, public for 24 h: breaks decision 4 | Cut-out and thumbnail are read and written once to their final keys at confirm (≤ 12 × ~300 KB); the staged ones are private names |
| Columns | One table | The piece repeats the item's analysed columns (a reflection test pins that every column the analysis sets exists on both) |
| Cleanup and deletion | Rows must be told apart from real items everywhere (`wardrobe-cleanup`, purger) | Delete the batch, the pieces cascade |

**Pick the tables.** A leak of private room-photo cut-outs, or of unconfirmed pieces into the league, is far worse than copying 12 small files.

`wardrobe.bulk_batches` (cascade on the user): `status` (`Created`, `Processing`, `Ready`, `Refused`, `Failed`, `Confirmed`, `Cancelled`, `Expired`),
`original_key`, `process_requested_at`, `counted` (counts toward the daily limit), `refusal`, `failure_code`, `failure_detail`, `analysis_attempts`,
`unseparated_count`, `unseparated_regions` (jsonb), `cut_off_count` (task BA2: pieces left out above the limit, see d), `vision_ms`, `embedding_model`, `expires_at` (created + 24 h), the counters for the metrics below
(`pieces_shown`, `pieces_pre_ticked`, `pieces_ticked`, `ticked_pre_ticked`, `category_edits`, `colour_edits`), `confirmed_at`, `confirmed_item_ids`
(uuid[]), `objects_deleted_at`, `ended_at` (task BA2: when it became `Confirmed`, `Cancelled` or `Expired`, because the tombstone's 7 days count from the
end, not from the creation). Indexes: `(user_id, created_at)`; partial `(expires_at)` over batches whose objects or staging are not yet cleaned up.
After the end the row stays as a tombstone for 7 days (the daily limit and the counters need it) and holds no image key and no piece.

`wardrobe.bulk_pieces` (cascade on batch **and** user): `id`, `sort_order`, `box_x/y/w/h`, `pre_ticked`, `reasons` (text[]), the analysed item columns
(`category_id`, `primary_color`, `secondary_color`, `pattern`, `formality`, `seasons`, `warnings`, `embedding vector(512)`, `embedding_model`,
`analysis_json`), `cutout_key`, `thumb_key`, `similar_item_id` (no FK: the hint is checked again when read). Deleted at confirm and cancel.

**One migration** (`BulkAdd`: both tables and indexes; nothing existing changes). Same PR: privacy notes 1a/2, the cascade, the
`AccountDeletionTests` matrix lines (`wardrobe.bulk_batches`, `wardrobe.bulk_pieces`).

**Keys.** `StorageKeys.BuildBulk(userId, batchId, ownerId, variant, ext)` → `bulk/{user}/{batch}/{owner}/{variant}.{ext}`; the photo is
`bulk/{user}/{batch}/{batch}/original.jpg`. New `ImageVariant`s `Staged` (`staged.png`) and `StagedThumb` (`stagedthumb.webp`): neither is
`cutout.*` or `thumb.*`, so `IsPublic` is false and `CacheControlFor` answers `private, no-store` (`StorageCacheControlTests` gets a `bulk/` case). The
status answer carries a **1-hour presigned GET** (`response-cache-control=private, no-store`). The prefix makes deletion trivial: confirm, cancel,
cleanup and `AccountPurger` delete **by listing `bulk/{user}/{batch}/`** (`IObjectStorage.ListAsync`), so a crashed delete or a lost row leaves nothing
the next run cannot find.

**The review image (owner's addition, task BA2): the staged image must be big enough to judge a cut. Chosen: a second, larger staged variant, not a promise
about the short side.** Vision's thumbnail is 400 px on its long side, too small to see whether an edge was cut well; its cut-out is a transparent PNG
cropped to the garment with its **long** side at most 1024 px (`kombin-vision/README.md`). A guarantee of "short side ≥ 600 px" cannot be made on the
backend: the cut-out is cropped to the garment, so a belt or a scarf is thin by nature, and nothing here may upscale (no image library beyond SkiaSharp's
`VisionImageFit`, and a bigger picture of the same pixels shows nothing more). So both are staged, privately: `staged.png` (the cut-out exactly as
vision sent it: **the review image**, long side up to 1024 px, short side ≥ 600 px for any garment that is at most about 1.7 times as long as wide, which is
every top, bottom, dress and jacket in the brief's photos) and `stagedthumb.webp` (vision's 400 px thumbnail, the grid and the "Dolabında benzeri var."
comparison). The status answer links **both** (`thumbUrl` for the grid, `imageUrl` for the review screen, each with its expiry), PR 2 adds a test that the
review image is the cut-out unchanged, and the confirm copies `staged.png` and `stagedthumb.webp` to the item's public `cutout.png` and `thumb.webp`
exactly like the single add, so nothing about the item changes. If the owner's 25 to 30 photos show that the review image is still too small for the
pieces that matter, that is a request to the vision team (a larger `maxSide` for `/v1/garment/bulk`), not a backend change.

## The job `bulk-add-analyze`

Fire-and-forget (`IBackgroundJobClient`), `[AutomaticRetry(Attempts = 0)]` (the retry is ours: one), a plain class like `WardrobeAnalyzeJob`, tests
call `AnalyzeAsync(batchId, ct)`. It claims the batch with a conditional update (`Processing` and not yet running), so a second run exits. Steps: read
the photo → `IVisionClient.BulkAsync` (new, in `Common/Vision`, 60 s timeout, parses `Retry-After`, `VisionCall` already carries the status and the
service's code) → on `503 busy` (or another transient failure) wait `Retry-After` (clamped 3 to 10 s) and **try once more**, then `Failed`
(`VisionBusy` or `VisionUnavailable`, retryable); a permanent 4xx → `Failed` `PhotoUnreadable` → a `refusal` → `Refused` and the photo is deleted → pieces:
**the same mapping as `WardrobeAnalyzeJob`** (`WardrobeAnalysis.Map`, same 0.35 category threshold, `Defaults`, `Warnings`, 512-float check, model from
`GetEmbeddingModelAsync`), cut-out and thumbnail written to the private staged keys, up to 12 pieces in reading order → `Ready` and the photo is deleted.
A bad answer (not base64, wrong embedding size) fails the batch (`AnalysisFailed`), like the single add. A job that died leaves `Processing`: `process`
may enqueue again after 10 minutes, and the cleanup marks one older than 15 minutes `Failed` `Interrupted`. The job holds a Hangfire worker for up to
~25 s while vision is busy; it is the only job that waits on a queue, so watch the worker count, not the code.

## c. "Already in your wardrobe?"

Yes, cheaply. The job, not the request, asks for each piece: the nearest of **the user's own** `Ready`, non-archived items of the same
`embedding_model` by `embedding <=> @piece` (`ORDER BY … LIMIT 1`; a user has a few hundred rows and `ix_items (user_id, …)` already narrows it, so
no new index and ≤ 12 queries of milliseconds). At or above `BulkAdd:DuplicateSimilarity` the piece stores `similar_item_id`. The status answer
carries `likelyDuplicate: { itemId, thumbUrl, message }` (the item's normal public thumbnail, a Turkish line "Dolabında benzeri var."), never a number
(lens rule: the app shows no percentage). **A hint, never a block:** `preTicked`, confirm and the limits ignore it. Start value **0.92**: the lens
uses 0.88 for a photo against a cut-out, and two cut-outs of the same garment from the same pipeline should score higher, but two white tees will too, so
the number is calibrated on the owner's 25 to 30 photos together with the stop rule. Pieces added from the catalog have no embedding of their own, so
v1 does not compare with them (the lens does it through the product; not worth the join yet).

## d. The status answer

`GET /{id}` is everything the review screen draws; no second call. Server-owned Turkish in **`BulkAddCopy`** (the one place, like `LeagueCopy`, pinned
by `BulkAddCopyTests`, added to `CopyGlossaryTests`' lists). The vision brief's sentences are copied verbatim; an unknown reason code from a newer vision
gets "Bu parçayı kontrol et." and its code, never an error. Request errors keep English `detail` and `errorCode` like every other route; the app maps those.

```
{ id, status, expiresAt,
  refusal:   { code, message } | null,                        // Refused
  failure:   { code, message, retryable } | null,             // Failed
  pieces: [ { id, thumbUrl, imageUrl, urlsExpireAt, box {x,y,w,h}, preTicked,       // thumbUrl = grid (400 px), imageUrl = review image (the staged cut-out, ≤ 1024 px)
              reasons: [ { code, message } ],                 // first one goes under the thumbnail
              category: { slug, name } | null, needsCategory, categoryCandidates: [ { slug, name } ],
              primaryColor, secondaryColor, pattern, warnings,
              likelyDuplicate: { itemId, thumbUrl, message } | null } ],
  unseparated: { count, message, regions: [ box ] } | null,   // "{count} parçayı ayıramadık; onları tek tek ekleyebilirsin."
  cutOff:      { count, message } | null,                     // owner's addition: vision found more pieces than the batch keeps
  items: [ WardrobeItemSummary ] }                            // only once Confirmed
```

**More pieces than the batch keeps (owner's addition, task BA2).** The batch keeps at most `BulkAdd:MaxPiecesPerPhoto` pieces in reading order and counts the
rest in `bulk_batches.cut_off_count` (the analysis job writes it; the service sends 12 at most, so this happens when the setting is lower or a newer service
sends more). The status answer then carries `cutOff { count, message }` with a server-owned sentence from `BulkAddCopy.CutOff(count)`: "Fotoğrafta {count}
parça daha vardı; bir seferde bu kadarını gösterebiliyoruz. Kalanları ayrı bir fotoğrafla ekleyebilirsin." It is Turkish, has no exclamation mark and no emoji,
says no limit (the limit is a setting) and, like every line, is pinned in `BulkAddCopyTests`. `cutOff` is null when nothing was left out; it is independent
of `unseparated` (garments the service saw but could not cut apart).

`category`, colours, pattern and `needsCategory` use the wardrobe item's own names and rules (`WardrobeItemResponses`), so the single add's category
chips and colour pickers work unchanged. `pieces` is empty unless `Ready`; `Ready` with no pieces but an `unseparated` is a valid answer.

## e. What the user can meet

| Situation | Answer |
|---|---|
| Flag off | 404 `Wardrobe.BulkDisabled` on every route |
| Photo not uploaded / over 8 MB / wrong type | 409 `Wardrobe.UploadMissing` / 400 `Wardrobe.ImageTooLarge` / 400 validation: the single add's own |
| Daily limit reached (`process`) | 429 `Wardrobe.BulkDailyLimit` with `retryAfterSeconds` to the next Istanbul midnight (the shape of `TryOnRenderGate`'s 429); the batch stays `Created`, nothing was spent |
| Three batches already open (`upload-url`) | 409 `Wardrobe.BulkTooManyOpen` (the app cancels what it abandons) |
| Vision refusal (7 codes) | status `Refused`, `refusal` code and the brief's sentence; counts toward the daily limit (the user's photo) |
| Vision busy twice, or down, or timed out | `Failed`, `VisionBusy` / `VisionUnavailable`, `retryable: true` ("Şu an çok yoğunuz; birazdan tekrar dene."); **not counted**; `process` again restarts it |
| Vision refused the file (4xx) | `Failed` `PhotoUnreadable`, `retryable: false` ("Bu fotoğrafı açamadık; başka bir fotoğrafla dene.") |
| Job lost | still `Processing`, then `Failed` `Interrupted` (retryable) after 15 minutes |
| Confirm with nothing ticked / unknown piece / bad category | 400 validation / `Wardrobe.BulkUnknownPiece` / `Wardrobe.UnknownCategory` (cancel is the way out) |
| Confirm before `Ready`, or after cancel or expiry | 409 `Wardrobe.BulkNotReady` / `Wardrobe.BulkExpired` |
| Storage down while copying at confirm | 503 `Wardrobe.StorageUnavailable`, nothing committed, written copies deleted, safe to retry |
| Someone else's batch or piece | 404 `Wardrobe.BulkNotFound` (never reveals it exists), a case in `AuthorizationMatrixTests` |

## Limits (settings in `BulkAdd:*`, `BulkAddOptions`, validated at start with every problem listed; a new number is a setting)

| Setting | Default | Why |
|---|---|---|
| `MaxPiecesPerPhoto` | 12 | vision's limit; extra pieces are cut off |
| `MaxPhotosPerDay` | 8 | counted `process` calls per Istanbul day, refusals included. 8 × 12 covers a whole wardrobe in a day; the cost is ~5 s of vision CPU a photo, and vision serves one bulk at a time (about 12 photos a minute for everybody) |
| `MaxOpenBatches` | 3 | bounds staging storage (3 × ~4 MB per user) and the jobs one user can queue |
| `StagingRetention` / `TombstoneRetention` | 24 h / 7 days | decision 4; the daily count needs a day |
| `DuplicateSimilarity` | 0.92 | calibrated, see c |
| `BusyRetryMin/Max`, `VisionTimeout` | 3 s / 10 s, 60 s | one retry (decision 2) |

**Everything else is as the single add**: no wardrobe-size limit exists today, so none is invented; the `uploads` policy and 8 MB apply.

## League usage points (decision 5)

Nothing to change, and a test pins it. `DayCloser` reads `PieceAdded` as a **boolean per player and day** (any `Ready`, not archived, not `not_garment` row
created in the 21:00 window), capped by `PieceDaysPerWeek` (3). Five pieces from one confirm are one flag: 5 points, once. Bulk items are created **at
confirm**, so the window is the confirm's, never the photo's, and a piece the analysis calls `not_garment` does not qualify, as today.
(`BulkAddLeagueTests`: confirm 5 pieces → one 5-point day; two more days → still 3 days a week.)

## f. Analytics and the stop rule

**Source of truth for the stop rule is the batch's counters** (server-side, immune to dropped or capped events), queried by hand in a `-- bulk:` block of
`docs/metrics.sql` (closed by the `-- league:` convention: not run by the job). Events (ids and enums, in `AnalyticsEventCatalog`, README rows,
`AnalyticsValidatorTests`) give the funnel and the timing:

| Event | Properties |
|---|---|
| `bulk_add_start` | `source` camera \| library |
| `bulk_add_result` | `status` Ready \| Refused \| Failed \| Timeout, `refusal?` (the 7 codes), `failure?`, `durationMs`, `pieces`, `preTicked`, `unseparated`, `duplicateHints` (0-12 each) |
| `bulk_add_confirm` | `shown`, `ticked`, `preTickedTicked`, `preTickedLeft` (pre-ticks the user unticked), `extraTicked` (not pre-ticked, ticked), `categoryEdits`, `colourEdits`, `durationMs` |
| `bulk_add_cancel` | `stage` Processing \| Review |

Measured per confirmed batch: pieces shown (`pieces_shown`), pre-ticked, ticked (`pieces_ticked`), unticked pre-ticks (`pieces_pre_ticked − ticked_pre_ticked`),
ticked extras, edits per ticked piece, refusals by code (`bulk_batches.refusal`). **Caveat for the stop rule:** an unticked pre-tick is a proxy for a wrong
one (the user may simply not want it), and the owner's labelling of the 25 to 30 photos stays the authority. No event carries a batch, piece or item ID, a box
or an image.

## Privacy and deletion

Staging is private, 24 h, and the photo seconds (objection above). `AccountPurger` deletes `bulk/{user}/` objects **before** the transaction (like the
wardrobe keys; a storage failure leaves the account for the next run); both tables cascade. `AccountDeletionTests`: matrix lines for both tables, a
batch with pieces and a photo → after the purge nothing under `bulk/{user}/` and no row. `docs/privacy-notes.md` 1a (what happens), 2 (the staged cut-outs
and embeddings: private names, presigned for the owner, 24 h), 4's style note on the photo, and CLAUDE.md "Wardrobe rules" get a bulk paragraph in PR 4.
The batch tombstones hold counts and enums only, no image key and no piece.

## Cleanup `bulk-add-cleanup`

Hourly at :17 `Europe/Istanbul`, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`, tests call `CleanupAsync(userId, now, ct)`.
Marks unfinished batches past `expires_at` `Expired` (pieces deleted), deletes the objects of every ended batch whose `objects_deleted_at` is null, deletes
tombstones after 7 days. **Registered even when `BulkAdd:Enabled` is false** (staging must still disappear if the flag is switched off); only
`bulk-add-analyze` is gated by the flag.

## Moderation

Same rule, later moment: the verdict only decides whether others may see a **wardrobe** image, so it is asked **after confirm** for the ticked pieces
only (a fire-and-forget `bulk-add-moderate` per batch calling `WardrobeModerator.ModerateAsync`, `Attempts = 0`; the daily `RetryMissingAsync` is the
safety net, as for the single add). Moderating at analysis would spend a vision call per piece the user never adds, on the service that has the second
slot reserved for single adds. Until the verdict is `Allow` the item is not shareable (`IsShareable`), exactly as a single add's first seconds.

## h. Pull requests and tests

| PR | Content | Tests |
|---|---|---|
| **PR 1 Foundation** | `BulkAdd:*` options, flag filter (the third copy of League's and TryOn's: **extract `FeatureEnabledFilter` to `Common/Hosting` here**, after B9 is merged, touching their two filters), migration and entities, `StorageKeys.BuildBulk` and the `Staged` and `StagedThumb` variants, `IVisionClient.BulkAsync` + fake (`FakeVisionClient.Bulk.cs`: flavours `bulk-3`, `bulk-refusal-<code>`, `bulk-busy`, `bulk-busy-twice`, `bulk-4xx`, `bulk-bad-embedding`), `BulkAddCopy`, errors, cascade lines | `BulkAddOptionsTests`, `BulkAddCopyTests`, `CopyGlossaryTests` lists, `StorageCacheControlTests` (`bulk/` keys private), `VisionClientBulkTests` (Retry-After parsing, 503 vs 4xx), `BulkPieceMirrorsItemTests`, `BulkAddDisabledTests` (404 on every route), `AccountDeletionTests` matrix lines |
| **PR 2 Start to status** | `upload-url`, `process`, `GET /{id}`, `bulk-add-analyze`, duplicate hint, limits, `AccountPurger` prefix delete, analytics catalog entries, `AuthorizationMatrixTests`, `OpenApiTests` | `BulkAddRulesTests` (pure: day allowance, clamp, counted, expiry, hint), `BulkAddStartTests`, `BulkAddAnalyzeJobTests` (ready, 7 refusals, busy then ok, busy twice, 4xx, bad answer, double run, interrupted, photo deleted on `Ready`/`Refused`, kept on `Failed`), `BulkAddStatusTests` (shape, presigned private thumbs, reasons, other user 404), `BulkAddLimitTests`, `BulkAddDuplicateHintTests` (own items only, other user's never), `AnalyticsValidatorTests`, `AccountDeletionTests` bulk objects |
| **PR 3 Confirm, cancel, cleanup** | `confirm`, `DELETE`, `bulk-add-cleanup`, `bulk-add-moderate`, counters | `BulkAddConfirmTests` (item equals a single add's: same fields, keys, `ConfirmedAt`; edits; same ID as the piece; retry returns the stored result; two concurrent confirms; confirm vs cancel; storage failure writes nothing; unticked gone; staged objects gone; items public only by `cutout.*`/`thumb.*`), `BulkAddParityTests` (the same photo through single and bulk gives equal attributes), `BulkAddLeagueTests`, `BulkAddCleanupTests` (24 h, flag off still cleans, tombstones, orphaned objects), `BulkAddModerationTests` (`IsShareable` only after `Allow`) |
| **PR 4 Docs and metrics** | `docs/metrics.sql` `-- bulk:` block, privacy notes, README, CLAUDE.md "Wardrobe rules" paragraph, security review line | `BulkMetricsSqlTests` (the block runs like `LeagueMetricsSqlTests`) |

PR sizes are reviewable (each ≈ 600 to 900 changed lines with tests); PR 2 needs PR 1, PR 3 needs PR 2, PR 4 can start with PR 3. No PR depends on the vision repo beyond its
README. `OutfitPerformanceTests` and the other latency budgets are untouched (no hot path changes).

## The owner's answers (2026-11-18) and what changed

The note was accepted. Answers to the open questions: **1 yes** (the room photo is deleted as soon as vision answered; kept only for a `Failed` batch),
**2 yes** (8 photos a day, 3 open batches, 12 pieces, 24 h), **3 yes as recommended** (a likely duplicate stays as vision ticked it, with the hint),
**4 yes**, **5 yes**, **6 yes**, **7 yes, but later** (done in the clean-up commit after B9: `FeatureEnabledFilter<TOptions>` in `Common/Hosting`, used by League, TryOn and bulk add), **8 yes**, **9 not in v1**. Two additions, built where they belong: the review image (see Keys: a
second, larger staged variant, `imageUrl` in PR 2) and the `cutOff` sentence (see d: the column and the copy are in PR 1, the answer in PR 2).

### PR 1 as built (task BA2)

Everything in section h's PR 1 row exists, with these differences from the plan, none of them a change of design:

- **The five routes are mapped now**, behind `FeatureEnabledFilter<BulkAddOptions>`, so "404 on every route" and the authorization matrix are real. With the flag on they
  answer 503 `Wardrobe.BulkNotImplemented` until PR 2 and PR 3 replace them (they are `BulkAddModule`'s placeholders, not use cases).
- **`IVisionClient.BulkAsync(image, contentType, timeout, ct)`** takes the timeout from the caller (`BulkAdd:VisionTimeout`, 60 s), so the vision client's own
  timeout is now a per-call one (30 s by default, unchanged for every other call) and `VisionCall<T>.RetryAfter` carries the parsed `Retry-After`.
- Two columns the plan did not list: `cut_off_count` and `ended_at` (above). `BulkAdd:*` also holds `ReprocessAfter`, `InterruptedAfter` and
  `StagedUrlLifetime`, the three numbers the job and cleanup sections name without a setting.
- `FakeVisionClient.Bulk.cs` has the six planned flavours and three more (`bulk-unreachable`, `bulk-many-<n>`, `bulk-unseparated-<n>`, plus `bulk-bad-image`).

### PR 2 as built (task BA3)

`upload-url`, `process`, `GET /{id}`, `bulk-add-analyze`, the duplicate hint, both limits, the purger's prefix delete and the four analytics events exist;
`confirm` and `DELETE` still answer 503 `Wardrobe.BulkNotImplemented` until PR 3. Differences from the plan, none of them a change of design:

- **The claim uses `analysis_attempts`** (no column was added): 0 means "not claimed", the job moves it 0 to 1 with a conditional update (a second run, even a
  simultaneous one, finds 0 rows and exits), 2 after the one retry; `process` resets it to 0. The photo's deletion on `Ready` and `Refused` keeps the key until
  the object is really gone (a failed delete is logged and leaves the key; the batch's prefix sweeps, PR 3 and the purger, remove it).
- **The daily allowance counts in-flight photos too**: the batches of the Istanbul day (by `process_requested_at`) that are `Counted` **or still `Processing`**,
  the batch itself excluded, so eight simultaneous `process` calls cannot all slip under the limit; one that fails stops counting. The open-batch count and the
  insert share a transaction under a per-user advisory lock (`bulk-open:{user}`), and a batch past its `expires_at` is not open even before the cleanup marks it.
- **The job commits `Ready` under the batch row lock** (`SELECT ... FOR UPDATE`, the lock confirm and cancel will take) and writes nothing when the batch is no
  longer `Processing`: staged objects of that run are deleted. Failures and refusals are conditional updates (`WHERE status = 'Processing'`).
- **`upload-url` reuses the single add's request record and validator** (`CreateUploadUrlRequest`), so the types, the 8 MB rule and the messages are the same code.
- **`GET` of a `Confirmed` batch already lists `items`** (from `confirmed_item_ids`, in order); nothing writes them until PR 3.
- **The hint is stored by the job and checked again by the status answer** (the item must still be the caller's own, `Ready`, not archived, with a thumbnail).
- **Tests**: `BulkAdd:Enabled` is on in `ApiFactory` (like the league and try-on) and `BulkAddDisabledTests` checks the off host on a host of its own;
  `BulkAdd:BusyRetryMin/Max` are 0 there (the clamp is `BulkAddRulesTests`). Compose: `docker/compose.yaml` sets `BulkAdd__Enabled` to `${BULK_ADD_ENABLED:-true}`
  (development), `docker/compose.prod.yaml` to `${BULK_ADD_ENABLED:-false}`; the code default stays false.

### PR 3 as built (task BA4)

`confirm`, `DELETE`, `bulk-add-cleanup` and `bulk-add-moderate` exist; `Wardrobe.BulkNotImplemented` is gone. Differences from the plan, none of them a change of design:

- **A category edit does not recompute formality and seasons**: section a says it does "exactly as `UpdateItem` does", but `PATCH /items/{id}` sets only the
  category, so the confirm does the same (the rule that matters is "the same as PATCH"; `ACategoryEditKeepsFormalityAndSeasonsLikePatch` pins it).
- **The copies are written inside the transaction, the prefix delete after the commit** (`BulkStaging`, shared by confirm, cancel and the cleanup): a storage
  failure while copying deletes the copies already written and rolls back (503, the batch stays `Ready`); a failed prefix delete only leaves
  `objects_deleted_at` null for the hourly cleanup. A repeated cancel of a batch whose objects could not be deleted tries the delete again.
- **The cleanup also does the `Interrupted` rule** (a batch `Processing` for longer than `BulkAdd:InterruptedAfter` becomes `Failed` `Interrupted`, retryable)
  and sweeps objects under `bulk/` whose batch row is gone or ended. It is registered by `UseWardrobeJobs` behind `Hangfire:Enabled` only, never behind the flag.
- **`bulk-add-moderate` moderates the batch's `confirmed_item_ids` without a verdict**, so a run after a repeated confirm does nothing new; a repeated confirm
  enqueues no second job.
- **Tests**: `BulkAddLeagueTests` also pins that a photo at 20:30 confirmed at 23:30 or 00:30 counts on the next league day (the confirm's window); the
  `bulk-single` vision flavour answers the single add's analysis so `BulkAddParityTests` compares the two paths.

## The questions as asked

1. **Delete the room photo as soon as vision answered**, not at confirm? **Yes** (kept only for a `Failed` batch so retry needs no re-upload).
2. **Numbers**: 8 counted photos a day, 3 open batches, 12 pieces, 24 h staging? **Yes**, all settings, change by environment variable.
3. **A likely duplicate starts unticked?** **No in v1**: show "Dolabında benzeri var." and leave the tick as vision said, so the stop rule stays measurable;
   decide after the 25 to 30 photos, which also calibrate 0.92.
4. **Unticked pieces are lost when you confirm** (one confirm per photo)? **Yes**; "add the rest later" would keep room-photo cut-outs for 24 h for a rare case.
5. **Does a refused photo use up the daily allowance?** **Yes**, a failed (our or vision's) one does not.
6. **Moderation after confirm** (a few seconds without a verdict, as the single add)? **Yes.**
7. **Extract the third `FeatureEnabledFilter` into `Common`** in PR 1? **Yes, once B9 is merged** (it touches the League and TryOn filters).
8. **Flag stays off in production** until the owner's photos pass the stop rule, on in development and staging first? **Yes.**
9. **A `GET /bulk` listing to resume after an app crash?** **Not in v1**: the app keeps the batch ID; the staging expires by itself.
