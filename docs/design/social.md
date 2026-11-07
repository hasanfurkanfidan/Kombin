# Social: OOTD posts, shoppable tags, creators (Task 14)

**The rule:** the feed is a shoppable, price-aware feed, never an Instagram clone. Every post answers *what is it, where is it cheapest,
does it go with what I own*. v1 has posts, tags, likes, follows, blocks, reports and creators. No comments, DMs, stories, video, hashtags or
search over posts.

## Data (schema `social`)

| Table | Key columns | Notes |
|---|---|---|
| `posts` | author, kind `Photo\|Outfit`, visibility `Private\|Followers\|Public`, status, audience (author's, snapshot), caption ≤ 300, consent version + time, moderation verdict/labels (jsonb)/reason, image keys (original, feed, preview) + aspect, outfit id, like/report counts, confirmed/published/hidden/deleted at | private by default |
| `post_tags` | post, box `x,y,w,h` (0-1), kind `Wardrobe\|Product\|Unknown`, wardrobe item / product, offer + price at tag time, label ≤ 60, proposal category/colour/confidence, source `Detected\|User\|Outfit`, `confirmed_at` | proposals have `confirmed_at = null` |
| `follows`, `likes`, `blocks` | composite PKs `(follower, followee)`, `(post, user)`, `(blocker, blocked)` | |
| `reports` | post, reporter, reason enum, created; unique `(post, reporter)` | reporter never shown to the author |
| `posting_consents` | user, version, **the text shown**, accepted at | append-only; text from `PostingConsent` |
| `creator_profiles` | user (PK), handle (unique, lower-case), bio ≤ 160, link, active | set by the founder only |
| `referral_events` | kind `Click\|Conversion`, creator, post, tag, click, product/offer/seller, viewer, share % snapshot, order/commission/creator amounts, external order ref | conversions are imported later |

`engagement.clicks` gains nullable `post_id` (no FK: a deleted post must not delete revenue records) and `creator_id` (FK, set null).

**Statuses.** `Processing` (job running) → `Blocked` (refused, `moderationReason` shown to the author) | `NeedsReview` (borderline) | `Draft`
(safe, waiting for the author's confirmation). Confirm: `Draft` → `Live`; `NeedsReview` stays there with `confirmed_at` set until a reviewer
decides. `Live` → `Hidden` at 3 reports. `Deleted` is a tombstone until the purge job removes objects and rows. `Draft` and `Failed` (the
photo could not be processed: unparseable, or the vision service stayed down) were added to the brief's list: "safe but not yet confirmed"
is a real state, and a post is never visible before its author confirmed tags and visibility.

**Indexes for the feed.** `posts (published_at DESC) WHERE status='Live' AND visibility='Public'` (for-you window), `posts (author_id,
published_at DESC) WHERE status='Live'` (following feed, profiles), `posts (author_id, created_at DESC)` (my posts), `follows (followee_id)`,
`blocks (blocked_id)`, `likes (user_id)`, `post_tags (post_id)`, `clicks (creator_id, clicked_at) WHERE creator_id IS NOT NULL`,
`referral_events (creator_id, occurred_at)`, unique `referral_events (click_id)`.

## Post flow

1. `GET/POST /social/consent`: the current versioned text; accepting stores the text and time. `upload-url` refuses without the current
   version (`Social.ConsentRequired`). A new text means a new version; every user accepts again.
2. `POST /posts/upload-url` (jpeg/png/webp ≤ 8 MB) → presigned PUT of the private original. `POST /posts/{id}/process` enqueues
   `post-process`; `POST /posts/from-outfit` builds an `Outfit` post from a saved outfit (tags from its pieces, collage boxes by slot).
3. `post-process` (fire-and-forget, retried like `wardrobe-analyze`): read the original → **strip metadata** (`ImageMetadata.Strip`: JPEG
   APP1/APP13/COM, PNG text/eXIf/tIME, WEBP EXIF/XMP; a JPEG orientation is kept as a 1-entry EXIF so the photo is not rotated) and write it
   back → `kombin-vision /v1/moderate?renditions=true` (verdict + a re-encoded ≤ 1600 px feed JPEG and a 400 px WEBP preview, no
   metadata) → `/v1/detect` (≤ 6 garment regions) → proposals. Outfit posts moderate each wardrobe cutout instead; catalog images are trusted.
4. `PUT /posts/{id}/publish` with the confirmed tags, caption and visibility. Proposals are deleted, the submitted tags are stored confirmed.
   **Viewers only ever get tags with `confirmed_at`**, and only after this call; the author sees the proposals in `GET /posts/{id}`.

**Detection (decision).** `/v1/detect` uses the models already loaded: the rembg person mask gives the silhouette box, which is cut into
anatomical bands (upper body, lower body, feet; half-body photos skip the feet). Each band is classified with CLIP restricted to that slot's
categories, plus a second guess for an outer layer, and coloured with the existing dominant-colour code. No box-detection model: it would
mean a new model, ~+300 MB and more latency, and boxes only need to be good enough for a dot the user moves or deletes. v2 if dots annoy users.

**Moderation.** One zero-shot CLIP pass over safe and unsafe label sets (`nsfw`, `nudity`, `violence`, `not_person_or_garment`); a pure
`decide()` maps scores to `allow | review | block` with thresholds documented in `kombin-vision/README.md`. Block thresholds are high (few
false blocks, the user sees a clear reason), review thresholds low (swimwear and underwear fashion land in review rather than being refused).
A vision outage keeps the post `Processing` and retries; it never publishes unmoderated.

## Feed ranking (`FeedRanking`, pure; candidates in SQL)

- **Visibility first, in SQL:** `Live`; `Public`, or `Followers` when the viewer follows the author; not blocked in either direction; not
  the viewer's own. Anonymous: `Public` only.
- `mode=following`: the 200 newest such posts by followed authors, newest first.
- `mode=forYou` (default): followed authors' posts from the last 72 h first (newest first, ≤ 30), then *sana göre*: SQL takes the 200 best
  of the last 14 days by `base = recency × popularity × creator`, then the page is reranked in memory:
  - `recency = exp(−ageHours / 48)`, `popularity = 1 + ln(1 + likes) / 4`, `creator = 1.3` for an active creator else 1 (cap 1.3);
  - `match` = mean `MatchScorer` score of the post's product tags for the viewer's active persona; posts below 60 (or without product
    tags) are dropped when the viewer has a persona that has learned from swipes (`MatchScorer` caps an unlearned persona at exactly 60,
    so the filter would empty the feed of every new user); same audience (viewer's or unisex) is a SQL filter;
  - `score = base × match / 100`; ties by newest, then id.
- **Never two posts by the same author adjacent:** a stable greedy pass takes, at each position, the first remaining post (in rank order)
  whose author differs from the previous one; posts that cannot be placed are dropped from this request.
- Pages of 20 over the ≤ 230 ranked posts (deeper history is on the author's profile). Each item carries `reason` (`Following|ForYou`) and
  its `matchScore`. Budget: p95 ≤ 250 ms at 50k posts (`FeedPerformanceTests`, `KOMBIN_PERF=1`).

## Shopping on a post

Product tags (and wardrobe tags whose item came from the catalog) show the product's **current cheapest sellable offer**
(`OutfitSources.CheapestOffersAsync`, effective price) with `goPath = /api/v1/go/{offerId}?src=feed&post={postId}`; no seller URL is ever
returned. The summary band is computed per viewer: `totalPrice` (sum of those offers), `pieceCount` (confirmed tags) and `ownedCount` (tags
the viewer owns exactly, or has a ready piece of the same category and colour; each tag says `exact|similar`). **Dolabımla dene**
(`POST /posts/{id}/try`) runs the how-to-wear engine with each tagged product as an anchor (≤ 3, top/bottom/outerwear/shoes first,
2 outfits each) against the caller's wardrobe; the engine core is extracted from `GET /how-to-wear`, not copied.

## Moderation & safety flow

Report (`Nudity|Violence|Hate|Harassment|Spam|Scam|Other`, once per user and post) → the third distinct reporter hides a `Live` post. Hidden
and `NeedsReview` posts wait in `GET /internal/social/review-queue`; `POST /internal/social/posts/{id}/review {approve|remove}` restores
(`Live`, or `Draft` if never confirmed) or blocks with the removal reason. No admin UI (out of scope); the founder uses the internal key.
Blocking removes follows both ways and hides each side's posts and profile from the other; the blocked user is not told.

## Creator economics

The founder sets `PUT /internal/social/creators/{userId}` (handle, bio, link, active). A creator's posts carry `isCreator` and the
disclosure line "Bu içerikten komisyon kazanabilir". `/go` with `post=` checks that the post is live, not private and tags the offer's
product; it then stamps `post_id`, and `creator_id` when the author is an active creator and not the clicker, and appends a `Click` referral
event with the `Creators:SharePercent` (40) snapshot. When conversions are imported (out of scope), each affiliate report row carries our
`kombin_click` id → a `Conversion` row with order, commission and `creator_amount = commission × share`. `GET /me/creator/stats` returns clicks,
top posts and **an estimate** `clicks × conversion rate × average order value × commission × share` (all `Creators:*` settings). The brief's
formula has no conversion rate; without one every click would count as a purchase and overstate earnings ~30×, so it is a setting
(`ConversionRatePercent`, default 3; 100 reproduces the brief literally).

## Left for v2

Comments (need moderation), follow requests / private accounts (in v1 anyone may follow, so "followers" means "anyone who follows me";
blocking is the remedy), post saves, similar-product suggestions for wardrobe/unknown tags, a box detector, seen-post tracking, editing a
live post's tags, payouts and conversion import, creator self-service bio editing, account deletion (a cross-feature task).
