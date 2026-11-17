# Privacy notes: every personal-data touchpoint (for counsel)

Scope: the Kombin backend (`kombin-backend`) and the vision service it calls (`kombin-vision`), as of Task 16-A (launch readiness: account deletion, the public legal pages, production configuration). Turkish
law (KVKK, Law No. 6698) and, for EU users, the GDPR are the frames in mind. This is an engineering inventory, not a legal opinion: it says
what we collect, why, where it lives, who can see it, how long we keep it and how it is deleted. Open questions are at the end.

## 1. Accounts (Identity)

| Data | Why | Where | Visible to | Retention / deletion |
|---|---|---|---|---|
| Email (lower-cased), password hash (never the password), display name | sign-in; the display name is shown on posts and profiles | `identity.users` | the user; the display name to anyone who can see one of their posts or their profile | until account deletion (section 1a: 7-day window, then removed) |
| Refresh tokens (hashed), device name, last login time | staying signed in | `identity.refresh_tokens`, `users.last_login_at` | nobody | revoked on logout; expired tokens kept until account deletion (section 1a) |

## 1a. Account deletion (Task 16-A)

`DELETE /api/v1/me` (the app's "Hesabımı sil"; the public page `/legal/delete-account` explains it) asks to delete the account. The user types "SİL" and,
for a password account, the password. The request takes effect at once, the data is removed 7 days later:

| When | What happens |
|---|---|
| The request | `users.deletion_requested_at` is set. Every refresh token is revoked, every device is deactivated (no push), every post that is or could become visible (live, under review, hidden, draft) is hidden from everyone (status `AccountPending`, the old status is remembered). Sign-in is refused (403) with the date. |
| The next 7 days | The user can take it back: `POST /auth/cancel-deletion` with the same credentials as sign-in (password, or a Google/Apple ID token) restores the account and the posts and signs the user in. The founder can do the same from `/admin/users` ("Cancel deletion"). |
| After 7 days | The hourly job `account-deletion` removes the account: first the stored objects, then, in one transaction, the rows below. If the storage cannot be reached nothing is touched and the next hour tries again. The founder can also remove an account at once ("Delete now", audited), e.g. for an e-mailed request. |

What happens to every table that holds data about a user:

| Table | At deletion |
|---|---|
| `identity.users` | deleted |
| `identity.external_logins` | deleted |
| `identity.refresh_tokens` | revoked at the request, deleted |
| `profile.user_profiles` (audience, measurements, city) | deleted |
| `profile.personas`, `profile.persona_style_profiles`, `profile.style_swipes` | deleted |
| `profile.size_entries` | deleted |
| `profile.size_prompts` (which products the user left the app for, from which seller, and how they answered "did you buy it, did the size fit?") | deleted (cascade). The click it came from is kept anonymised like every click |
| `engagement.saved_products`, `price_alerts`, `alert_triggers` | deleted |
| `notification.devices` (push tokens) | deactivated at the request, deleted |
| `notification.notifications`, `notification_preferences` | deleted |
| `wardrobe.items`, `wardrobe.wears` | deleted; the objects (original, cutout, thumbnail) are deleted from storage first |
| `recommendation.outfits`, `outfit_items`, `outfit_feedback` | deleted (budget outfits included) |
| `recommendation.trips` (destination city, dates, plans per day, the packing plan) | deleted |
| `social.posts`, `post_tags` | deleted; the objects (original, feed image, preview) are deleted from storage first |
| `social.likes` | the user's likes and the likes on the user's posts are deleted; the like counters of other people's posts go down by one |
| `social.reports` | the reports the user made are deleted (the report counter of the reported post is left as it is: the report was made); reports on the user's posts go with the posts |
| `social.follows`, `social.blocks` | deleted in both directions |
| `social.posting_consents` | deleted (the proof of consent goes with the account; see open question 11) |
| `social.creator_profiles` | deleted |
| `league.players`, `league.consents` (joins and leaves with the consent text shown), `league.daily_scores`, `league.group_members` (with the weekly alias), `league.style_scores` | deleted (cascade) |
| `league.entries`, `league.entry_pieces` | deleted (cascade); the league's copies of the outfit thumbnails are deleted from storage first (their keys carry no user ID: `AccountPurger` finds them through the entries) |
| `league.pairs` (the user's votes, and pairs that showed the user's outfits), `league.reports` (made by the user, or on the user's outfits) | deleted (cascade); other players' closed days keep their stored scores |
| `league.voter_days` (the user's served pairs and counted votes per day) | deleted (cascade); other voters' counters are their own rows, so their voting points of an open day do not change when the user's outfit (and the pairs that showed it) goes |
| `league.engine_preferences` | not personal data (engine products and a weight, no user ID); untouched |
| `tryon.renders` (mannequin drawings: they **reveal body proportions**; the cache key and a hash of the body values) | deleted (cascade); the images are deleted from storage first |
| `tryon.garments` of the user's wardrobe items (what the vision service read from the photo: outline points, flags) | deleted (cascade with the wardrobe items); the prepared cutouts are deleted from storage first. Catalogue garments are catalogue data, untouched |
| `tryon.reports` ("Yanlış görünüyor": which outfit or product, which garments, which renderer) | deleted (cascade) |
| `engagement.clicks` | **anonymised**: `user_id`, `persona_id` and (as a creator) `creator_id` become null; the rest stays (offer, product, seller, source, post ID, user agent, salted IP hash) as the revenue record |
| `analytics.events` | **anonymised**: `user_id` becomes null; `anonymous_id` (a random install ID) stays (open question 12) |
| `social.referral_events` | **kept** as the financial record: `creator_id` and `viewer_user_id` become null, the amounts stay |
| `admin.audit_log` | kept; it holds no personal data of app users, only the deleted user's ID as a target |
| `analytics.weekly_metrics`, `recommendation.narrations`, `profile.style_cards`, the catalog | not personal data; untouched |
| Hangfire job tables | job rows hold IDs for the days Hangfire keeps them, then expire by themselves |
| Backups | the database backups (`scripts/backup.sh`) are kept 7 days, so deleted data is gone from them 7 days after the removal |
| Logs | request logs and the reverse proxy's access log (client addresses) rotate by size (5 x 10 MB per container); they are not searched or edited per user |

Tested in `AccountDeletionTests` (every table above for one user, other users untouched, the storage objects gone, the retry after a storage failure,
cancelling inside the window, the admin buttons).

## 2. Style profile, sizes, wardrobe (Profile, Wardrobe)

| Data | Why | Where | Visible to | Retention |
|---|---|---|---|---|
| Audience (women/men/unisex), measurements, city, personas, swipes, sizes per brand | recommendations; the city fetches the weather | `profile.*` | the user only; the audience is copied onto posts for feed matching (never shown) | until account deletion (section 1a) |
| Wardrobe photos: private original, public cutout and thumbnail, garment attributes, a CLIP embedding | the wardrobe and outfits | object storage `wardrobe/{user}/{item}/…`, `wardrobe.items` | the original: the user (15-minute presigned link); cutout/thumbnail: **anyone with the URL** (unguessable keys, public bucket policy); the URL is given to other users (a post's wardrobe tag, an outfit post) only after the safety filter allowed the cutout (`wardrobe.items.moderation_verdict`, security review M3) | archived items' images deleted after 30 days; abandoned uploads after 1 day |
| Wear dates | wear tracking | `wardrobe.wears` | the user | until account deletion (section 1a) |
| Saved trips: name, destination city, dates, plans per day (office, evening, ...), the packing plan (wardrobe item IDs, the weather used) | "Valiz hazırlama" | `recommendation.trips` | the user only (`/me/trips` is scoped to the caller; another user's ID is 404) | until the user deletes the trip or the account (section 1a); a plan that is only planned, not saved, is not stored |
| Budget outfits: the outfits suggested for a budget (pieces, prices at suggestion time) | "Bütçeye göre kombin" | `recommendation.outfits` (kind `Budget`), only for signed-in callers; the anonymous route stores nothing, and the budget amount itself is not stored | the user | as other outfits (section 1a) |

The original wardrobe photo is sent to `kombin-vision`, which processes it in memory and stores nothing; the cutout then goes to its `/v1/moderate` (allow / review / block, only the verdict and its time are stored). Wardrobe originals are **not**
metadata-stripped (they are private and never shown); see open questions.

## 3. Social posts (Task 14)

### 3.1 Consent

Before the first post the user accepts a versioned consent text (`PostingConsent`, Turkish). We store **the version, the exact text shown
and the time** (`social.posting_consents`, append-only). Every post records the consent version and time it was created under. A new text
is a new version and everyone must accept again before posting. The text tells the user: they confirm everyone in the photo agreed; posts
are private by default; EXIF/location is removed; every photo goes through an automatic safety check and doubtful ones are seen by a person;
tag suggestions are never shown without their confirmation; deletion is permanent within 24 hours; creators may earn commission. Current
version: `2026-11-v2` (November 2026; the wording of `2026-11-v1` with "akışta" replaced by "uygulamada" per the app glossary; every poster
accepts it again, v1 acceptances stay in history).

### 3.2 The photo

| Step | What happens to personal data |
|---|---|
| Upload | The app PUTs the photo straight to private object storage (`posts/{user}/{post}/original.{jpg,png,webp}`, presigned URL, 15 min). JPEG, PNG and WEBP only (HEIC is converted on the phone). Until processing the stored file still has its metadata; unprocessed uploads are deleted after 1 day. |
| Metadata removal | `post-process` strips the original **before anything else reads it** and writes the stripped file back over it: JPEG EXIF (GPS position, camera serial, timestamps), XMP, IPTC, comments, embedded thumbnails and extra pictures (MPF); PNG text/eXIf/time chunks; WEBP EXIF/XMP. Only a non-upright orientation is kept, as a one-entry EXIF. A file that cannot be parsed is refused, never stored unstripped. (`ImageMetadata`, unit-tested.) |
| Safety check | The stripped photo goes to `kombin-vision /v1/moderate` (our own service, same network, nothing stored, nothing logged but scores). Zero-shot labels: explicit content, nudity, violence, off-topic. Result: allow / review / block, stored with the scores (`posts.moderation_json`). |
| Renditions | The vision service re-encodes a feed image (≤ 1600 px JPEG) and a 400 px preview (WEBP) **from pixels only**: no metadata of any kind survives. Both are stored **private** (`feed.jpg`, `preview.webp`) and served through presigned GETs valid for 1 hour, so a followers-only or private post cannot be fetched by a URL that leaked. |
| Tag proposals | `kombin-vision /v1/detect` finds up to 6 garment regions (category, colour; no face or identity analysis). Proposals are visible to the author only and deleted when the author publishes. |

No face recognition, no biometric templates, no person identification, no age or gender estimation is done anywhere.

### 3.3 Visibility and access

- New posts are **Private**. The author chooses Private, Followers ("Takipçilerime") or Public ("Herkese açık") when publishing.
- Public: anyone, including signed-out users of the app. Followers: users who follow the author (**follows are not approved in v1**:
  anyone can follow; the author can block; the API documentation says so and the app words the option accordingly, security review M5). Private: the author only.
- Not visible to anyone but the author unless `Live`: processing, drafts, posts under review, blocked and hidden posts.
- A block (either direction) hides each side's posts and profile from the other and removes follows. The blocked user is not told.
- There are no public web pages for posts (no indexing by search engines); everything is behind the app's API.

### 3.4 Interactions

| Data | Where | Visible to | Retention |
|---|---|---|---|
| Follows (who follows whom, when) | `social.follows` | follower/following counts are public; the lists are not exposed by the API | until unfollow, block or account deletion |
| Likes | `social.likes` | the like count is public; who liked is not exposed (only "liked by me") | deleted with the post |
| Reports (reporter, reason, time, whether it counts toward hiding: decided from the reporter's account age and persona count, which are not stored with it) | `social.reports` | the operator (review queue shows reasons and counts, not reporters); never the author | deleted with the post |
| Blocks | `social.blocks` | the blocker (their own list) | until unblock or account deletion |
| Captions (≤ 300 chars), unknown-tag labels (≤ 60 chars) | `social.posts`, `social.post_tags` | as the post | deleted with the post. Free text: may contain personal data the user types; it is never sent to analytics. |

### 3.5 Moderation by people

Borderline photos, hidden posts and live posts with any report since their last review wait in `GET /internal/social/review-queue` (operator only, internal key; a 1-hour preview link,
the moderation scores, report reasons). The reviewer approves or removes; the author sees a fixed reason, not who decided. There is no admin
UI: reviewers are the founder/operators with the internal key.

### 3.6 Deletion

- `DELETE /posts/{id}`: the post is gone from every API response at once (tombstone).
- The hourly `social-purge` job then deletes the stored original, feed image and preview, and the post row with its tags, likes and reports.
  Hourly runs make the promise **"permanently deleted within 24 hours"** hold even if several runs fail.
- Also purged: uploads never processed (1 day), failed posts (1 day), drafts never published (7 days), blocked posts (30 days, kept for
  questions about the decision).
- Kept after deletion: buy clicks (`engagement.clicks`) and creator referral events keep the post's ID (a random UUID, no image, no text):
  they are the revenue and commission record. Storage backups (if the provider keeps any) follow the provider's retention.
- Deleting the **account** deletes all the user's posts the same way (section 1a), after the 7-day window; they are hidden at once.

## 4. Visual search ("Görselden ürün bul", Task 15)

`POST /api/v1/lens/search` takes a photo (camera or gallery; often of a person in the street or of a screen) and answers the garments in it
and the closest catalog products. Anonymous callers may use it.

| Data | Why | Where | Retention |
|---|---|---|---|
| The query photo | find the garments and their embeddings | **nowhere**: held in the API's memory for the request and sent to `kombin-vision`, which stores nothing either; never logged | gone when the request ends |
| Region embeddings (512 numbers per garment) | the catalog and wardrobe search | memory only | gone when the request ends |
| Signed in: the caller's profile audience and wardrobe embeddings are *read* | filter the catalog; "dolabında benzeri var" | `profile.user_profiles`, `wardrobe.items` (nothing written) | — |

The photo only becomes stored data when the user taps "Dolabıma ekle": the app then crops the region **on the device** and uploads the crop
through the normal wardrobe flow (section 2), with its usual rules. Taps on results go through `/go` with `src=lens` (section 5). Analytics
(`lens_open`, `lens_search`, `lens_result_tap`, `lens_add_to_wardrobe`) carry counts, durations, positions and enums, never image data.

Catalog product images are downloaded by the `product-embeddings` job (`KombinBot` user agent, one request per host per second) to compute
the catalog's embeddings; they are sellers' product shots, not personal data, and only the URL and the vector are kept.

## 5. Buy clicks and creators (Engagement, Social)

| Data | Why | Where | Retention |
|---|---|---|---|
| Click: user (or none), offer/product/seller, source (`feed`), post ID, creator ID, persona ID, user agent, **salted SHA-256 of the IP** (never the IP), correlation ID, time | affiliate revenue, fraud checks, creator attribution | `engagement.clicks` (append-only) | kept; the user ID is set to null if the account is deleted |
| Referral event: creator, post, tag, click, product/offer/seller, clicker's user ID (or none), share %, later order/commission amounts | creator commission | `social.referral_events` (append-only) | kept (financial record); clicker set to null on account deletion |
| Size question: user, product, seller and offer of the user's first click on the product, the click's time, the answer (fits, too small, too large, did not buy, do not ask again) and the size named in it. Created from the user's own clicks 3 days after the click when the user has not switched the questions off (`profile.sizePromptsEnabled`); expires after 30 days; one per product | remembering the user's size per brand ("Beden hafızası"); no push, no e-mail | `profile.size_prompts` | until the account is deleted (cascade); the size it taught is in `profile.size_entries` |
| Creator profile: handle, bio, link, active flag | the creator page | `social.creator_profiles` | until the founder deactivates it or the account is deleted |

Creators see aggregate numbers only (`GET /me/creator/stats`: clicks, an estimate, top posts) — never who clicked. Creator posts show "Bu
içerikten komisyon kazanabilir" (advertising disclosure). Affiliate URLs carry our click ID, never user identifiers.

## 5a. Admin panel (Task 10b)

The founder's server-rendered panel (`/admin`). Admin accounts are configuration (`Admin:Users`: an e-mail and a password hash), not rows of
`identity.users`, and the panel never uses the app's tokens.

| What the panel shows | Where | Minimisation |
|---|---|---|
| A user's e-mail, display name, sign-in methods, counts of personas, wardrobe items, posts and blocks, creator handle | `/admin/users` | e-mail addresses appear **only** on this page (the creator invite page prints them masked, `f***@gmail.com`); no measurements, no city, no sizes, no wardrobe photos, no list of what the user owns |
| Post photos waiting for review | `/admin/review` | the **feed-size** rendition only, a 1-hour presigned link; never the original (which is stripped of metadata anyway); author shown by display name and ID |
| Report reasons and moderation scores | `/admin/review` | reporters are never shown, only the reasons and counts |
| What an admin did | `admin.audit_log` (admin e-mail, action, target ID, short summary, time) | no personal data of app users in the summary: IDs, handles, enum names and the removal reason the author was shown; kept indefinitely (open question below) |

Disabling an account (soft) revokes its refresh tokens and refuses sign-in; it deletes nothing. The account's posts stay visible according to
their own rules. Account deletion (section 1a) is also on `/admin/users`: it shows a pending request and offers "Delete now" and "Cancel deletion", both
audited (the audit summary holds counts, never e-mail addresses).

## 6. Analytics (Analytics)

Allow-listed events only (`AnalyticsEventCatalog`); values are IDs, numbers and enum names, never free text. After an account is deleted its events lose
their `user_id` (section 1a). Social events: `post_view`,
`post_tag_tap`, `post_like`, `post_create_start/done`, `post_report`, `creator_profile_view`, `try_with_wardrobe` — post and creator IDs,
counts and enums; **no captions, handles or labels**. Visual-search events: counts, durations, result positions and match levels, **no
image data**. Keys named like personal data are refused on every event. Anonymous events are linked to
the account after sign-in (`IAnalyticsIdentityLinker`). **Retention:** raw events are deleted 180 days after they were received (`analytics-retention`, nightly, `Analytics:RetentionDays`); the weekly aggregates (`analytics.weekly_metrics`, no personal data) stay. At most 5,000 events per user or device a day are stored (security review M6).

## 6a. Rate limiting and server logs (Task 16-A)

The rate limiter (`RateLimits:*`) counts requests per user ID (signed in) or per client address in the API's memory only: nothing is written, and a
restart forgets everything. The address is the real client's when the reverse proxy (Caddy) forwards it. The reverse proxy writes an access log
(client address, path, status) to its container log, rotated by size; the API's own log has correlation IDs and paths, not addresses, e-mail
addresses or tokens. Neither is searched per user.

## 6b. Kombin Ligi (League, tasks L2, L3 and L4; design `docs/design/league.md`)

Off until the app screens ship (`League:Enabled` false). Task L2 built joining and leaving, task L3 the entries, the image copies, voting and
reports, task L4 the day close, the daily points, the results and the purge, task L5 the weekly groups, the table, the style score, the review
queue and the result push.

| What | Why | Where | Who sees it | How long |
|---|---|---|---|---|
| Join and leave records, with the exact consent text shown and its version (current `2026-11-v2`, which says that someone who knows the pieces can tell whose outfit it is) | proof of the consent and of the 18+ declaration | `league.consents`, `league.players` | the operator | until the account is deleted |
| Birth year (read, not stored again) | a known year joins (and enters an outfit) only when it makes the player certainly 18 | `profile.user_profiles` | nobody new | as the profile |
| The day's worn outfit (its pieces, which outfit, the occasion, the profile's audience) and copies of the pieces' thumbnails | the outfit competes | `league.entries`, `entry_pieces`, object storage `league/{random}/{random}/thumb.*` | **other players**, without a name, a profile or any stable identifier: only the piece images (slot and URL), under keys that hold no user ID and differ every day, as files that differ in bytes from the wardrobe thumbnail and from every other copy (see below); only when every piece passed the safety filter (`IsShareable`); a saved outfit never enters, only a worn one | copies: deleted within minutes of withdrawing or leaving (`league-entries`), otherwise 7 days after the day (`league-purge`, daily 04:15); a copy no entry names (a crash, a failed delete) a day after it was written; entries 90 days after a closed day |
| Votes (which of two outfits or a skip, the server's and the app's decision time, the weight) | the day's ranking | `league.pairs` | the operator; nobody sees who voted; the voter sees the chosen outfit's win rate of the day | 30 days after a closed day (`league-purge`); until then, until the account is deleted |
| The voter's counters of the day (pairs served, votes that counted) | the 30-pair cap and the voting points, which must not shrink when another player's account (and the pairs that showed their outfit) is deleted | `league.voter_days` | the voter (`servedPairs`, `countedVotes`); the operator | 30 days after a closed day (`league-purge`); until the account is deleted |
| Daily points: whether the outfit competed and was ranked, its rank among N, comparisons, percentile, the four point parts, whether it is under review or was removed by a review | the evening result (`GET /league/results`, the result push) and the weekly table | `league.daily_scores` (one row per player and closed day with an entry or a point) | the player only (their own row and entry; the results never read another player's row); the operator | until the account is deleted (closed days never change; the entry link becomes empty when the entry is purged after 90 days) |
| Engine preference rows | the engine learns which catalog outfits win | `league.engine_preferences` (written by the close for every counted vote between an engine outfit and a player's) | the operator | no user ID at all; the player's pieces are never stored there, only the engine outfit's products |
| Reports on an outfit (optional reason) | taking unsafe outfits out of the pool | `league.reports` | the operator (`GET /internal/league/review-queue`; a decision is written to `admin.audit_log` with the entry ID and no personal data) | until the outfit or either account is deleted |
| Week groups with a generated alias ("Yeşil Trençkot": a random colour and garment, new every week, unique in the group, never derived from the name, e-mail or ID), week points, final place, promotion or relegation | the weekly table (`GET /league/table`) | `league.group_members`, `league.groups`, `league.weeks` | the group sees `{ rank, points, isMe, alias }` per row, never a name, e-mail, ID or anything about an outfit; leaving takes the player off the current week's table | until the account is deleted (cascade; the others' ranks are renumbered on read) |
| The style score (mean and deviation of the percentiles of 90 days) and the league part of creator eligibility | the founder decides creators (no screen, no endpoint) | `league.style_scores` | the operator only | until the account is deleted |
| The evening result push (rank or "yeterli oy toplanamadı", the day's total) | the result | `notification.notifications` (kind `LeagueResult`), `league.daily_scores.notified_at` | the player | as every notification (section on notifications); off with the `leagueResults` preference |

**What the image copies hide, and what they do not** (task L3-fix). A copy is the wardrobe thumbnail with a block of 16 to 271 random bytes added
where every decoder skips it (`ImageMarker`: a JPEG comment, a private PNG chunk, an unknown WEBP chunk). So a machine comparing files finds
nothing in common: the SHA-256, the store's ETag (the MD5 of the file) and the exact size differ between two copies of the same piece, and
between a copy and the thumbnail a Social post shows for that piece (which carries the author's name and ID). What remains, stated plainly:
- **a person can still recognise the same photo by eye**: the same garment photographed once looks the same in the league, on another day and
  on a post that tags it;
- the compressed image data inside the file is byte-for-byte the source's, so someone who downloads both files and strips the container (or
  compares them with a perceptual hash) can still match them; only re-encoding the pixels would change that, and there is no image library in
  the API (the vision service could re-encode, at one call per piece per day; not done in v1);
- the size differs by at most a few hundred bytes from the source, so it narrows a search without deciding it.
Catalog pieces are catalog images (the same for everyone who owns the product) and link to no one.

So the league's promise is **no name, no profile and no stable identifier in any response**, not that an outfit cannot be traced to its owner.
Consent version `2026-11-v2` tells the player exactly that: "Parçalarını tanıyan biri kombinin sana ait olduğunu anlayabilir." Players who joined
under version 1 are asked again and enter no outfit until they accept.

The league schema is **not** readable by Metabase (`metabase_ro`); its first-month metrics are SQL run by hand. Leaving stops every future entry and
withdraws the day's outfit; deleting the account removes everything above (section 1a).

## 6c. "Manken üstünde gör" (TryOn, task P3; design `docs/design/try-on.md`)

Off until the app screens ship (`TryOn:Enabled` false). The feature draws an outfit on a mannequin **proportioned from the user's measurements**:
the drawing is a catalogue figure, not a photo of the user, but its proportions are the user's, so it is treated like the measurements themselves.

| What | Why | Where | Who sees it | How long |
|---|---|---|---|---|
| Prepared garments of the user's wardrobe items: a cutout of the garment alone, outline points, the service's flags (no photo of a person: an item whose photo had someone in it is never prepared) | the drawing | `tryon.garments`, object storage `tryon/{user}/{garment}/prepared.png` (private by name; read by the server only, never given out) | nobody; the server sends it to `kombin-vision` when drawing | as the wardrobe item (cascade); deleted with the account |
| The drawing (a WEBP of the figure wearing the outfit) and a hash of the body values it was made for | "Manken üstünde gör" | `tryon.renders`, object storage `tryon/{user}/{render}/render.webp` (private by name) | **the user only**: a presigned GET valid for one hour, given only in the answer to the user's own request; another user's outfit is 404 | deleted when the user's measurements or audience change (at the next drawing at once, otherwise within a minute by `tryon-prepare`), 30 days after it was last shown, and with the account |
| Reports ("Yanlış görünüyor"): which outfit or product, which garments, the renderer | finding garments drawn wrong (a top shown from the back) | `tryon.reports` | the operator (`docs/metrics.sql`, `tryon_reported_garments`, which shows a wardrobe garment without anything of its owner) | until the account is deleted |

The measurements (height, weight, chest, waist, hip, audience) go to `kombin-vision` with each new drawing; it stores nothing. They are never written to a log line or an
analytics event: the vision client logs only the path and the service's error code, the job and the renderer log IDs and codes. No analytics event was
added. Catalogue garments (products) are catalogue data and hold nothing about users.

## 7. Processors and transfers

| Recipient | What | Where |
|---|---|---|
| Object storage (MinIO on the server; R2 possible) | photos and renditions | the hosting provider's EU region (the plan: one EU VPS) |
| Backup bucket (S3-compatible, e.g. Cloudflare R2) | the encrypted nightly database dump (age or AES-256; the key is not on the server), kept 7 days | the provider's EU or default jurisdiction: **to be chosen** (open question 6) |
| `kombin-vision` | photos for analysis, moderation and detection, visual-search query photos, garment cutouts and **body measurements** for "Manken üstünde gör"; stores nothing | our own service, same deployment |
| Let's Encrypt | the domain names, for the TLS certificates (no user data) | Let's Encrypt |
| Expo Push Service | push tokens and notification text | Expo (US) |
| Open-Meteo | the profile's city, and a trip's destination with its dates (forecast and archive APIs; no user ID) | Open-Meteo (EU) |
| Anthropic (only if `Narration:Provider = Anthropic`) | outfit summaries (garment categories/colours, no photos, no user data) | Anthropic (US) |
| Affiliate networks / sellers | the redirect with our click ID | the seller's site |

Photos of posts and visual-search photos are never sent to a third party.

## 8. Open questions for counsel

1. **Data export** (KVKK m. 11 access) is not built; access requests are answered by hand from the e-mail address on the legal pages. Account deletion is built
   (section 1a): is a 7-day cancellation window with the account closed meanwhile acceptable as "deletion on request", or must a request that cannot be taken
   back be offered as well?
2. Do photos of people (and of third parties in them) need consent beyond the poster's confirmation in the consent text?
3. Is open follow (no approval) acceptable for "Takipçilerime" posts, or do we need follow requests before launch?
4. Retention of moderation scores and of blocked posts (30 days) and of reports after a post is deleted (deleted with it today).
5. Wardrobe originals keep their EXIF (private, never shown). Strip them too?
6. Storage region and cross-border transfer (KVKK Art. 9) for the chosen provider; Expo and Anthropic are US-based.
7. Creator commission disclosure wording and whether creators need a separate agreement (payouts are not built).
8. Minors: there is no age gate.
9. Visual search processes photos of other people (a stranger in the street) in memory only, never stored. Is an in-app notice on the
   lens screen enough, and should anonymous use need anything more?
10. Admin audit log (`admin.audit_log`) is kept indefinitely and holds the removal reasons authors were shown. How long should it be kept, and is a
    founder's access to e-mail addresses on `/admin/users` enough of a justification on its own (it is logged only as the user ID of an action)?
11. Account deletion deletes the posting-consent records (the proof that a user accepted a text version). Should proof of consent be kept (anonymised) for a limitation
    period for posts that were public?
12. `analytics.events.anonymous_id` and a click's user agent and salted IP hash survive deletion. They no longer point at an account, but they are pseudonymous
    identifiers of an install or a connection. Delete or null them too?
13. The 13-year limit in the legal texts is a placeholder: there is still no age gate (question 8), and Turkish law and the stores may require something else.
14. The reverse proxy's access log holds client addresses for as long as the container logs rotate (about 50 MB). Is that a documented retention, or should the proxy not
    log addresses at all?
15. Backups keep deleted data for up to 7 days after the removal. Is "within 7 days of the removal" an acceptable statement, and should the legal pages say so as precisely?
16. The public legal pages are drafts written from this document; `docs/legal-review.md` lists every sentence flagged for a lawyer.
17. **Kombin Ligi consent, versions `2026-11-v1` and `2026-11-v2` (section 6b): not reviewed by counsel.** The owner's current text (v2):
    "Katılırsan dolabındaki parçaların fotoğrafları diğer oyunculara gösterilir. Adın ve profilin görünmez. Parçalarını tanıyan biri kombinin
    sana ait olduğunu anlayabilir. İstediğin gün kombinini çıkarabilir, ayarlardan ligden ayrılabilirsin. Katılarak 18 yaşından büyük olduğunu
    onaylarsın." (v1 was the same without the third sentence.) Its last sentence is the only age declaration (an unknown birth year may join; a known birth year
    joins only when it makes the player certainly 18, so a player born in 2008 waits until 2027). Is it enough as consent for showing wardrobe photos to strangers and as an 18+ declaration? A changed
    text becomes the next version and every player accepts again.
18. **"Manken üstünde gör" (section 6c).** A drawing shows the user's body proportions (from their own measurements). It is private, served for an hour at a
    time, deleted when the measurements change and 30 days after it was last shown. Is that retention right, and does entering measurements need a notice
    that they also shape this drawing (the measurements screen does not say so today)?
