# Privacy notes: every personal-data touchpoint (for counsel)

Scope: the Kombin backend (`kombin-backend`) and the vision service it calls (`kombin-vision`), as of Task 15 (visual search). Turkish
law (KVKK, Law No. 6698) and, for EU users, the GDPR are the frames in mind. This is an engineering inventory, not a legal opinion: it says
what we collect, why, where it lives, who can see it, how long we keep it and how it is deleted. Open questions are at the end.

## 1. Accounts (Identity)

| Data | Why | Where | Visible to | Retention / deletion |
|---|---|---|---|---|
| Email (lower-cased), password hash (never the password), display name | sign-in; the display name is shown on posts and profiles | `identity.users` | the user; the display name to anyone who can see one of their posts or their profile | until account deletion (**not built yet**, see open questions) |
| Refresh tokens (hashed), device name, last login time | staying signed in | `identity.refresh_tokens`, `users.last_login_at` | nobody | revoked on logout; expired tokens kept until account deletion |

## 2. Style profile, sizes, wardrobe (Profile, Wardrobe)

| Data | Why | Where | Visible to | Retention |
|---|---|---|---|---|
| Audience (women/men/unisex), measurements, city, personas, swipes, sizes per brand | recommendations; the city fetches the weather | `profile.*` | the user only; the audience is copied onto posts for feed matching (never shown) | until account deletion |
| Wardrobe photos: private original, public cutout and thumbnail, garment attributes, a CLIP embedding | the wardrobe and outfits | object storage `wardrobe/{user}/{item}/…`, `wardrobe.items` | the original: the user (15-minute presigned link); cutout/thumbnail: **anyone with the URL** (unguessable keys, public bucket policy) and anyone who sees an outfit post using that piece | archived items' images deleted after 30 days; abandoned uploads after 1 day |
| Wear dates | wear tracking | `wardrobe.wears` | the user | until account deletion |

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

## 6. Analytics (Analytics)

Allow-listed events only (`AnalyticsEventCatalog`); values are IDs, numbers and enum names, never free text. Social events: `post_view`,
`post_tag_tap`, `post_like`, `post_create_start/done`, `post_report`, `creator_profile_view`, `try_with_wardrobe` — post and creator IDs,
counts and enums; **no captions, handles or labels**. Visual-search events: counts, durations, result positions and match levels, **no
image data**. Keys named like personal data are refused on every event. Anonymous events are linked to
the account after sign-in (`IAnalyticsIdentityLinker`).

## 7. Processors and transfers

| Recipient | What | Where |
|---|---|---|
| Object storage (MinIO in development; S3/R2 planned) | photos and renditions | hosting provider — **region to be decided** |
| `kombin-vision` | photos for analysis, moderation and detection, visual-search query photos; stores nothing | our own service, same deployment |
| Expo Push Service | push tokens and notification text | Expo (US) |
| Open-Meteo | the profile's city (no user ID) | Open-Meteo (EU) |
| Anthropic (only if `Narration:Provider = Anthropic`) | outfit summaries (garment categories/colours, no photos, no user data) | Anthropic (US) |
| Affiliate networks / sellers | the redirect with our click ID | the seller's site |

Photos of posts and visual-search photos are never sent to a third party.

## 8. Open questions for counsel

1. **Account deletion and data export** are not built (a cross-feature task). Until then deletion requests are manual.
2. Do photos of people (and of third parties in them) need consent beyond the poster's confirmation in the consent text?
3. Is open follow (no approval) acceptable for "Takipçilerime" posts, or do we need follow requests before launch?
4. Retention of moderation scores and of blocked posts (30 days) and of reports after a post is deleted (deleted with it today).
5. Wardrobe originals keep their EXIF (private, never shown). Strip them too?
6. Storage region and cross-border transfer (KVKK Art. 9) for the chosen provider; Expo and Anthropic are US-based.
7. Creator commission disclosure wording and whether creators need a separate agreement (payouts are not built).
8. Minors: there is no age gate.
9. Visual search processes photos of other people (a stranger in the street) in memory only, never stored. Is an in-app notice on the
   lens screen enough, and should anonymous use need anything more?
