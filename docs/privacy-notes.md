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
| `engagement.saved_products`, `price_alerts`, `alert_triggers` | deleted |
| `notification.devices` (push tokens) | deactivated at the request, deleted |
| `notification.notifications`, `notification_preferences` | deleted |
| `wardrobe.items`, `wardrobe.wears` | deleted; the objects (original, cutout, thumbnail) are deleted from storage first |
| `recommendation.outfits`, `outfit_items`, `outfit_feedback` | deleted |
| `social.posts`, `post_tags` | deleted; the objects (original, feed image, preview) are deleted from storage first |
| `social.likes` | the user's likes and the likes on the user's posts are deleted; the like counters of other people's posts go down by one |
| `social.reports` | the reports the user made are deleted (the report counter of the reported post is left as it is: the report was made); reports on the user's posts go with the posts |
| `social.follows`, `social.blocks` | deleted in both directions |
| `social.posting_consents` | deleted (the proof of consent goes with the account; see open question 11) |
| `social.creator_profiles` | deleted |
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
| Wardrobe photos: private original, public cutout and thumbnail, garment attributes, a CLIP embedding | the wardrobe and outfits | object storage `wardrobe/{user}/{item}/…`, `wardrobe.items` | the original: the user (15-minute presigned link); cutout/thumbnail: **anyone with the URL** (unguessable keys, public bucket policy) and anyone who sees an outfit post using that piece | archived items' images deleted after 30 days; abandoned uploads after 1 day |
| Wear dates | wear tracking | `wardrobe.wears` | the user | until account deletion (section 1a) |

The original wardrobe photo is sent to `kombin-vision`, which processes it in memory and stores nothing. Wardrobe originals are **not**
metadata-stripped (they are private and never shown); see open questions.

## 3. Social posts (Task 14)

### 3.1 Consent

Before the first post the user accepts a versioned consent text (`PostingConsent`, Turkish). We store **the version, the exact text shown
and the time** (`social.posting_consents`, append-only). Every post records the consent version and time it was created under. A new text
is a new version and everyone must accept again before posting. The text tells the user: they confirm everyone in the photo agreed; posts
are private by default; EXIF/location is removed; every photo goes through an automatic safety check and doubtful ones are seen by a person;
tag suggestions are never shown without their confirmation; deletion is permanent within 24 hours; creators may earn commission.

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
  anyone can follow; the author can block). Private: the author only.
- Not visible to anyone but the author unless `Live`: processing, drafts, posts under review, blocked and hidden posts.
- A block (either direction) hides each side's posts and profile from the other and removes follows. The blocked user is not told.
- There are no public web pages for posts (no indexing by search engines); everything is behind the app's API.

### 3.4 Interactions

| Data | Where | Visible to | Retention |
|---|---|---|---|
| Follows (who follows whom, when) | `social.follows` | follower/following counts are public; the lists are not exposed by the API | until unfollow, block or account deletion |
| Likes | `social.likes` | the like count is public; who liked is not exposed (only "liked by me") | deleted with the post |
| Reports (reporter, reason, time) | `social.reports` | the operator (review queue shows reasons and counts, not reporters); never the author | deleted with the post |
| Blocks | `social.blocks` | the blocker (their own list) | until unblock or account deletion |
| Captions (≤ 300 chars), unknown-tag labels (≤ 60 chars) | `social.posts`, `social.post_tags` | as the post | deleted with the post. Free text: may contain personal data the user types; it is never sent to analytics. |

### 3.5 Moderation by people

Borderline photos and posts with 3 reports wait in `GET /internal/social/review-queue` (operator only, internal key; a 1-hour preview link,
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
the account after sign-in (`IAnalyticsIdentityLinker`).

## 6a. Rate limiting and server logs (Task 16-A)

The rate limiter (`RateLimits:*`) counts requests per user ID (signed in) or per client address in the API's memory only: nothing is written, and a
restart forgets everything. The address is the real client's when the reverse proxy (Caddy) forwards it. The reverse proxy writes an access log
(client address, path, status) to its container log, rotated by size; the API's own log has correlation IDs and paths, not addresses, e-mail
addresses or tokens. Neither is searched per user.

## 7. Processors and transfers

| Recipient | What | Where |
|---|---|---|
| Object storage (MinIO on the server; R2 possible) | photos and renditions | the hosting provider's EU region (the plan: one EU VPS) |
| Backup bucket (S3-compatible, e.g. Cloudflare R2) | the encrypted nightly database dump (age or AES-256; the key is not on the server), kept 7 days | the provider's EU or default jurisdiction: **to be chosen** (open question 6) |
| `kombin-vision` | photos for analysis, moderation and detection, visual-search query photos; stores nothing | our own service, same deployment |
| Let's Encrypt | the domain names, for the TLS certificates (no user data) | Let's Encrypt |
| Expo Push Service | push tokens and notification text | Expo (US) |
| Open-Meteo | the profile's city (no user ID) | Open-Meteo (EU) |
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
