# "Arkadaşına sor" (ask a friend): design (task AF1)

The owner's brief: a user who is unsure about an outfit sends it to a friend in a few taps, the friend answers quickly, the user sees the
answer. It must stay "a few taps in the morning" and it must bring new people in, so the friend may not have the app. Roadmap: "iki kombin
arasında oylama linki, girişsiz oy, API'nin sunduğu herkese açık sayfa; kötüye kullanım kontrolü ayrı." Launch is early February 2027, code
cut-off 10 January 2027: two backend PRs and two mobile PRs (section 9). Everything below is decided; the owner's open items are in section 10,
each with the default the implementation uses.

## 1. The flow

1. On an outfit (today's suggestion, a saved or a worn outfit) the user taps **"Arkadaşına sor"**. The question is "Bu kombin olur mu?". A chip
   "+ İkinci kombin" adds a second outfit and makes it "Hangisi?".
2. The first time, a sheet shows the consent text (section 3); "Paylaş" accepts it and creates the ask in one call. Later asks skip the sheet.
3. The app opens the phone's share sheet with the server's `shareText` and the link. The user picks WhatsApp (or anything else).
4. The friend opens `https://api.<domain>/sor/{token}` in any browser, no sign-in, no app: sees the pieces, taps A/B (or Olur/Olmaz), may add a
   name and a short note, sends. The page then shows the count so far and "Sen de sor: Kombin'i indir".
5. The asker gets a push ("Arkadaşların cevap veriyor: A 2, B 1") and sees every answer on the ask's screen. The link closes by itself after
   24 hours, or when the user taps "Linki kapat"; the copied pictures are deleted then.

## 2. Decisions (questions a-c)

| | Decision | Why |
|---|---|---|
| Who can be asked | **A share link only** in v1, sent through the phone's share sheet. Friends who have the app use the same web page. No in-app recipient picker. | One path for everyone, and it is the path that brings new users. At launch the follow graph is empty, and Social's follows are open (anyone may follow without approval), so "my followers" is not "my friends" and a push to them would be a spam channel. An in-app picker is v2 (section 10). |
| What the friend sees | The question line, per outfit the pieces as images (slot order), "A"/"B" labels for a pair, the answer buttons, optional "Adın" and "Not ekle", a report link, the privacy link. **No name, no profile, no prices, no shop links, no user or wardrobe ID.** | The friend knows who sent it (the chat). A forwarded link shows nothing more about the asker than the clothes. |
| Which pictures | A wardrobe piece shows a **private copy** of its thumbnail, only when `WardrobeModerator.IsShareable` (verdict `Allow`); a catalog piece shows its product's primary image (a retailer image); any other piece is a grey tile with the slot name ("Ayakkabı"). An outfit with nothing to show is refused. | The same safety bar as posts and the league. A piece added this morning and not yet moderated does not block the ask. |
| How long | `AskFriend:AnswerWindow` (24 h) after creation, or until the user closes it, or until reports hide it. Then the page says "Bu soru kapandı" (410) and the copies are deleted. | A morning question is decided the same day; a question asked the evening before is still open the next morning. |
| The question | **Both**: one outfit (`Single`, "Bu kombin olur mu?", answers `Yes`/`No`) or two (`Pair`, "Hangisi?", answers `A`/`B`). | The same tables and page; only the choice set differs. The roadmap's "two outfits" is the featured path; "is this one ok?" is the most common morning question. |
| The answer | One tap, plus optional name (≤ 24 characters) and note (≤ 140). No chat, no reply to a note. A second answer from the same browser **replaces** the first. | Fast to answer, nothing to moderate in real time. |

## 3. Consent and privacy

Versioned and append-only like `PostingConsent` and `LeagueConsent` (`AskFriendConsent.Versions`, the text hash pinned in `AskFriendConsentTests`,
scanned by `CopyGlossaryTests`). Version **`2026-11-v1`**:

> Linki alan herkes, giriş yapmadan bu kombinlerdeki parçaların fotoğraflarını görebilir ve cevap verebilir. Adın ve profilin sayfada
> görünmez. Link 24 saat sonra kendiliğinden kapanır; istersen daha önce kapatabilirsin. Link kapanınca fotoğraflar silinir. Linki
> paylaşarak 18 yaşından büyük olduğunu onaylarsın.

It names the window, so changing `AnswerWindow` means a new consent version (`AskFriendOptionsTests` says so in its message). Acceptance is part
of `POST /me/asks` (`consentVersion`): the first ask under a version writes one `askfriend.consents` row with the exact text. "Taking the link
back" is `POST /me/asks/{id}/close`: the page answers 410 at once and the copies are deleted in the same request.

**Minors.** The league's strict rule: a known birth year blocks unless `year − birthYear > AskFriend:MinAge` (18) in the Istanbul year → 403
`AskFriend.AgeRestricted`; an unknown year may ask (the consent is the declaration). `LeagueRules.IsUnderAge` is pure and now has two users, so
it moves to `Common/Time/AgeRule.IsUnderAge` (shared code rule) and League calls it from there. The friend answering is never asked for anything
but an optional name; section 10 has the open item.

**Pictures outside the app.** Copies live under `ask/{random}/{random}/shared.webp` (a new `ImageVariant.Shared`; `shared.*` is not public by the
bucket policy, `StorageKeys.IsPublic` stays false). The page embeds a presigned GET of `AskFriend:ImageUrlLifetime` (15 min), signed on every
page load (the page is `no-store`). Deleting the object ends every URL already given out. No `ImageMarker`: the asker is not anonymous to the
friend, the copy exists so the URL holds no user ID and can be revoked. No Open Graph image (a chat app's preview would keep the outfit on its
servers): the preview is a fixed title "Hangisi? Bir dokunuşla cevap ver" and the Kombin icon.

**Records:** `docs/privacy-notes.md` gets section **6d** (the table of section 4 below: who sees what, how long), question 19 for counsel (the consent
text and the friends' names and notes, data of people without an account), and section 1a's deletion table gets the `askfriend` rows.

## 4. Data (schema `askfriend`, entities in `Persistence/Entities/AskFriend`)

| Table | Columns | Notes |
|---|---|---|
| `asks` | `id`, `user_id` (FK cascade), `token` (text, 22, unique), `kind` `Single\|Pair`, `status` `Open\|Closed\|Hidden`, `consent_version`, `created_at`, `expires_at`, `closed_at`, `copies_deleted_at`, `page_views`, `store_clicks`, `push_count` | index `(user_id, created_at DESC)`; `(expires_at) WHERE copies_deleted_at IS NULL` for the purge |
| `pieces` | `id`, `ask_id` (cascade), `option` `A\|B` (a `Single` ask has only `A`), `outfit_id` (FK set null), `slot` (`OutfitSlot`), `position`, `wardrobe_item_id` (FK set null), `product_id` (FK set null), `copy_key` (null when none), `image_url` (catalog image, else null) | one row per shown slot, built at creation |
| `answers` | `id`, `ask_id` (cascade), `voter_hash` (SHA-256 hex of the browser cookie), `choice` `Yes\|No\|A\|B`, `name` ≤ 24, `note` ≤ 140, `created_at`, `updated_at`, `notified_at`, `hidden_at` | unique `(ask_id, voter_hash)`; index `(notified_at) WHERE notified_at IS NULL` |
| `reports` | `id`, `ask_id` (cascade), `voter_hash`, `reason` `Nudity\|Harassment\|Spam\|Other`, `created_at` | unique `(ask_id, voter_hash)` |
| `consents` | `user_id` (cascade), `version`, `text`, `accepted_at` | PK `(user_id, version)` |

`OutfitSlot` is a Persistence enum, so naming it is not a feature reference. The status `Closed` is stored only for "closed by the user"; an
ask past `expires_at` is answered as `Expired` by the pure `AskState.Of(ask, now)` (`Open`, `Expired`, `Closed`, `Hidden`), the one place for it.

**The token** is 16 bytes from `RandomNumberGenerator`, base64url without padding (22 characters, 128 bits). It is stored as is, not hashed:
it opens one 24-hour page of clothes and a vote, never an account, and the asker's app must be able to share it again (`url` in the response).

## 5. API (`Features/AskFriend`, group `/api/v1/me/asks`, `.RequireAuthorization()`, `FeatureEnabledFilter<AskFriendOptions>`)

Every route answers 404 `AskFriend.Disabled` first while `AskFriend:Enabled` is false. Rate limit: new policy `askfriend` (30 per hour per
user) on create and close; the rest under the global fallback.

| Route | Request | Success | Errors |
|---|---|---|---|
| `GET /me/asks/consent` | — | 200 `{ version, text, accepted }` | — |
| `POST /me/asks` | `{ outfitIds: [guid] (1 or 2, distinct, non-empty), consentVersion: string }` | 201 `AskResponse`, `Location: /api/v1/me/asks/{id}` | 400 `Validation.Failed`; 404 `AskFriend.OutfitNotFound` (not the caller's, any status but `Dismissed` allowed); 409 `AskFriend.ConsentRequired` (not the current version); 403 `AskFriend.AgeRestricted`; 409 `AskFriend.NothingToShow` (an outfit with no image to show); 409 `AskFriend.DailyLimit` (`MaxAsksPerDay` asks in the Istanbul day); 503 `AskFriend.StorageUnavailable` (nothing is kept) |
| `GET /me/asks?page=&pageSize=` | paging (`PagedRequest`) | 200 `PagedResponse<AskSummary>`, newest first | — |
| `GET /me/asks/{id}` | — | 200 `AskResponse` | 404 `AskFriend.NotFound` (not the caller's) |
| `POST /me/asks/{id}/close` | — | 204 (idempotent; also on an expired or hidden ask) | 404 `AskFriend.NotFound` |
| `DELETE /me/asks/{id}/answers/{answerId}` | — | 204 (sets `hidden_at`; idempotent) | 404 `AskFriend.NotFound` |

```
AskResponse { id, kind: "Single"|"Pair", status: "Open"|"Expired"|"Closed"|"Hidden", statusMessage: string|null,
  url: string|null (only while Open), shareText: string, createdAt, expiresAt,
  options: [{ option: "A"|"B", outfitId: guid|null, pieces: [{ slot, imageUrl: string|null }] }],
  tally: { yes, no, a, b, total },            // hidden answers are not counted
  answers: [{ id, choice: "Yes"|"No"|"A"|"B", name: string|null, note: string|null, answeredAt }] }   // newest first, not hidden
AskSummary { id, kind, status, createdAt, expiresAt, total, leading: "Yes"|"No"|"A"|"B"|null }   // null on a tie or no answer
```

The asker's `imageUrl` is their own wardrobe thumbnail or the catalog image (the owner may see their own pictures; it also works after the copies
are gone); null when the piece no longer exists. `shareText` and `statusMessage` are server copy in `AskFriendCopy` (glossary-scanned, pinned by
`AskFriendCopyTests`): `"Bugün hangisini giyeyim? Bir dokunuşla cevap ver:"` / `"Bu kombin olur mu? Bir dokunuşla cevap ver:"`; `Hidden` →
`"Link bildirimler yüzünden kapatıldı."`. Creation copies the wardrobe thumbnails in the request (≤ 2 outfits, ≤ 16 pieces; a failed copy
deletes the copies already written and answers 503); no job.

AF2 as built: the 16-piece cap is split per outfit (16 for one, 8 each for a pair) so outfit B can never be cut to nothing; an outfit item
whose wardrobe piece is not shareable falls back to its catalog image when it has one; `answeredAt` is the answer's `updated_at` (a
replacement moves it; AF3 must set `notified_at` with a bulk update so the push does not). `Notifications:AskAnswers:MaxPushesPerAsk` moves to
AF3 with the push job (there is no Notification options class to hold it yet).

## 6. The public page (Razor Page, `Features/AskFriend/Pages/Ask.cshtml`, `@page "/sor/{token}"`)

Razor Pages already serve the legal pages from the `/Features` root (`AddRazorPages` in `AdminModule`); only `/Admin/Pages` asks for a sign-in,
so this page is public by default. No JavaScript at all: plain forms, works in any browser. Caddy needs no change (the catch-all proxies it).
`[IgnoreAntiforgeryToken]`: there is no session to protect, and a forged vote is no different from a vote by anyone who has the link.

| Route | Answers |
|---|---|
| `GET /sor/{token}` | 200 page; 404 page for a malformed or unknown token, a `Hidden` ask, an asker who is disabled or pending deletion, or the feature off; 410 page "Bu soru kapandı" for `Expired`/`Closed`. Increments `page_views`. After an answer (`?cevap=1`) it shows the thanks, the tally and the download links. |
| `POST /sor/{token}` | form `choice` (`Yes\|No` for `Single`, `A\|B` for `Pair`), `name?`, `note?` → upsert by `voter_hash` → **303** to `GET /sor/{token}?cevap=1`. 400 page (choice not valid for the kind), 409 page "Bu soru yeterince cevap aldı" (`MaxAnswers` reached; a replacement is still allowed), 410/404 as above. |
| `POST /sor/{token}/bildir` | form `reason` → one report per browser → 303 to `GET /sor/{token}?bildirildi=1`. The `HideAt`-th report sets `Hidden` and deletes the copies in the request. |
| `GET /sor/{token}/indir?p=android\|ios` | increments `store_clicks`, 302 to `AskFriend:StoreLinks:Android`/`Ios` (the Play link carries `referrer=utm_source%3Dkombin_ask`); always 302, even for an unknown token. |

**The browser cookie** `kaf` (16 random bytes, base64url; `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/sor`, 7 days) is set on the first
GET; only its SHA-256 is stored. A browser without cookies gets a new one each time; accepted (this is advice, not a contest).

**Headers** (set by the page, its own short list; Admin's middleware is scoped to `/admin`): `Cache-Control: no-store`, `X-Robots-Tag: noindex,
nofollow`, `Referrer-Policy: no-referrer` (the token must not leak to the store or a retailer), `X-Frame-Options: DENY`, `X-Content-Type-Options:
nosniff`, CSP `default-src 'none'; img-src https: data:; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`
(`http:` added in Development for MinIO).

**Page copy** (in `AskFriendCopy`): "Hangisi?" / "Bu kombin olur mu?", buttons "A" / "B" or "Olur" / "Olmaz", "Adın (isteğe bağlı)", "Not ekle
(isteğe bağlı)", "Gönder", "Teşekkürler! Cevabın iletildi.", "Bu soru kapandı.", "Sen de sor: Kombin'i indir", "Uygunsuz içerik bildir".

## 7. Abuse (question c)

| Risk | Control |
|---|---|
| Guessing a link | 128-bit token; malformed tokens are 404 before the database; policy `askfriend-public` (60 per 5 min per IP, in memory) on all four page routes; the global fallback under it. No IP is stored. |
| Forwarding | Accepted and said in the consent ("Linki alan herkes"). Bounded by the 24 h window, `MaxAnswers` (50), the close button and the absence of any name or ID on the page. |
| Answers from strangers | Only the asker reads answers; one answer per browser; the asker hides any answer (`DELETE .../answers/{id}`), which also takes it out of the tally. |
| The note and name text | `AskNoteRules` (pure): trimmed, control characters removed, length limits; a link (`http`, `www.`, `something.tld/`) or a word on `AskNoteRules.Blocked` (a short Turkish slur list in code, pinned by its test, glossary-exempt like legal text) **drops the note or name, never the vote**, and the thanks line says "Notun eklenmedi". Shown as text only (Razor encodes; the app renders plain text). |
| Hosting unsafe pictures | Only `Allow`-verdict wardrobe pieces and catalog images are ever shown; no upload path of its own. |
| Reports | Anyone with the link may report once per browser; `AskFriend:Reports:HideAt` (3) reports hide the ask at once (the asker's own link only; no review queue in v1, `docs/metrics.sql` counts reports). |
| Spamming asks | `MaxAsksPerDay` (10) per user and Istanbul day, the `askfriend` policy, at most 2 outfits. |
| Minors | The strict age rule on creation (section 3). |
| Push spam to the asker | At most `Notifications:AskAnswers:MaxPushesPerAsk` (5) pushes per ask, one per job run. |

## 8. Push, jobs, deletion, analytics, switch (question d)

**Push.** Notification gets kind `AskAnswer`, preference `AskAnswers` (column `ask_answers`, default true; `askAnswers` in the preferences
request and response) and the job **`ask-answers`** (`Features/Notification/Jobs`, every minute, `[DisableConcurrentExecution(60)]`,
`[AutomaticRetry(Attempts = 0)]`, tests call `ProcessAsync(now, userIds, ct)`): un-notified, not hidden answers of asks whose user is neither disabled
nor pending deletion → one notification per ask (`Copy.ForAskAnswers`: Pair `"Arkadaşların cevap veriyor: A 2, B 1."`, Single `"Arkadaşların cevap
veriyor: 2 olur, 1 olmaz."`, title "Arkadaşına sor"), dedupe key `ask:{askId}:{push_count + 1}`, `DispatchRules.Decide` (preference, devices, quiet
hours), `push_count` +1, `notified_at` set on those answers in the same transaction; past the cap the answers are only marked. `Data` adds `askId`
and `deepLink: "kombin://asks/{askId}"` (additive). It reads `askfriend` tables through `KombinDbContext`, names no AskFriend type (like
`league-results`). Worst-case delay from answer to push: about two minutes (this job, then `push-sender`).

**`askfriend-purge`** (`Features/AskFriend/Jobs`, every 15 minutes, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`, registered by
`UseAskFriendJobs` only when `Hangfire:Enabled` and `AskFriend:Enabled`; tests call `PurgeAsync(now, askIds, sweepOrphans, ct)`): deletes the copies of
asks that expired, were closed or hidden (`copies_deleted_at`), deletes asks (and by cascade pieces, answers, reports) `AskFriend:Retention:AskDays` (30)
after creation, and objects under `ask/` that no piece names once older than `AskFriend:Retention:OrphanGrace` (1 day).

**Account deletion.** Every table cascades on the user (through `asks`); `AccountPurger` deletes the copies first, found through
`askfriend.pieces` of the user's asks (a query, no AskFriend type). While deletion is pending or the account is disabled, the page is 404 and
the push job skips the user. `AccountDeletionTests`' matrix gets `askfriend.asks`, `pieces`, `answers`, `reports`, `consents` and a check that the
copies are gone from `FakeObjectStorage`. The friends' names and notes go with the asker's ask.

**Analytics** (catalog entries, ids and enums only; `AnalyticsValidatorTests` pins the enums spelled out): `ask_create { askId, kind: Single|Pair }`,
`ask_share { askId }` (share sheet completed), `ask_view { askId, status: Open|Expired|Closed|Hidden }`, `ask_close { askId }`, `ask_answer_hide { askId }`.
The web page sends nothing (no script, no tracker); `docs/metrics.sql` gets a `-- askfriend:` block run by hand: asks per active user, share rate,
page views and answers per ask, minutes to the first answer, store clicks per page view. Metabase gets no access to the schema.

**Settings** (`AskFriendOptions`, `AskFriend:*`, validated at start, defaults pinned by `AskFriendOptionsTests`): `Enabled` false · `PublicBaseUrl`
(required and https in production: a `ProductionSettings` check when enabled) · `AnswerWindow` 24 h · `MaxAsksPerDay` 10 · `MaxAnswers` 50 ·
`MinAge` 18 · `ImageUrlLifetime` 15 min · `Reports:HideAt` 3 · `Retention:AskDays` 30 · `Retention:OrphanGrace` 1 day · `StoreLinks:Android`,
`StoreLinks:Ios`. Plus `Notifications:AskAnswers:MaxPushesPerAsk` 5 and `RateLimits:AskFriend` (30/h per user), `RateLimits:AskFriendPublic`
(60 per 5 min per IP), pinned in `RateLimitTests`.

**Switch.** `AskFriend:Enabled` (env `AskFriend__Enabled`), default **false**: routes stay mapped (OpenAPI, the authorization matrix) and answer
404 `AskFriend.Disabled`; the page answers 404; the jobs do nothing. `ApiFactory` switches it on. The app calls `GET /me/asks/consent` once per
session and hides the entry point on 404.

## 9. Boundaries and pull requests (questions e and f)

**Boundaries.** A feature of its own, `Features/AskFriend` (module `AskFriendModule`: `AddAskFriendFeature`, `MapAskFriendEndpoints`,
`UseAskFriendJobs`). It reads `recommendation.outfits`/`outfit_items`, `wardrobe.items`, `catalog.products` and `profile.user_profiles` through
`KombinDbContext` and names exactly **one** other feature's type: Wardrobe's `WardrobeModerator` (`IsShareable`), one new allow-list line
`Kind.Seam` pointing at the CLAUDE.md "Ask-a-friend rules" section. The age rule moves to `Common/Time/AgeRule` (shared code, no line). Nothing
references AskFriend: Notification, Identity and Analytics only touch its tables or spell out its enums. **No `Legacy` line.** CLAUDE.md gets an
"Ask-a-friend rules" section, the internal/out-of-scope lists name the page as the one public page besides the legal pages (Social keeps "no
public web pages for posts"), and `docs/security-review.md` gets the five app routes and four page routes in its surface map.

**Backend PR AF2: data, consent and the asker's API.** Schema and migration (all five tables), options, consent, `AgeRule` move, the five `/me/asks`
routes, copies, `ImageVariant.Shared`, the `askfriend` policy, analytics catalog, purger step, authorization matrix, privacy notes 6d and 1a.
Tests: `AskFriendOptionsDefaultsMatchTheDesignNote`, `AskFriendConsentTextHashIsPinned`, `AgeRuleBlocksABirthYearThatIsNotCertainlyAdult`,
`AskTokenIs22UrlSafeCharacters`, `SharedImageVariantIsNotPublic`, `AskStateIsExpiredAfterTheAnswerWindow`, `CreateAskForOneOwnOutfitReturnsALink`,
`CreateAskWithTwoOutfitsIsAPair`, `CreateAskForAnotherUsersOutfitIs404`, `CreateAskWithAnOldConsentVersionIs409`, `FirstAskStoresTheConsentTextOnce`,
`CreateAskForAKnownMinorIs403`, `UnmoderatedPieceHasNoImage`, `OutfitWithNothingToShowIs409`, `AskOverTheDailyLimitIs409`,
`CopiesAreStoredUnderKeysWithoutTheUserId`, `CloseDeletesTheCopiesAtOnce`, `ClosingTwiceIs204`, `AskListShowsOnlyTheCallersAsks`,
`AskRoutesAre404WhileDisabled`, `AccountDeletionRemovesAsksAndCopies`, `AskFriendRateLimitDefaultsArePinned`, and the matrix and allow-list updates.

**Backend PR AF3: the page, answers, push and purge.** The Razor page and its three other routes, `AskNoteRules`, the `askfriend-public` policy,
reports and hiding, `ask-answers` with kind, preference and copy, `askfriend-purge`, the metrics block, CLAUDE.md, README, security review.
Tests: `NoteWithALinkIsDropped`, `NoteWithABlockedWordIsDropped`, `NoteControlCharactersAreRemoved`, `SingleAskAcceptsOnlyYesOrNo`,
`PageShowsThePiecesForAnOpenAsk`, `PageCarriesNoUserIdNameOrWardrobeKey`, `UnknownTokenIs404`, `ExpiredAskPageIs410`, `ClosedAskPageIs410`,
`PageIs404WhileTheAskerIsPendingDeletion`, `PageIs404WhileDisabled`, `PageSendsNoStoreNoIndexAndNoReferrer`, `AnswerRedirectsWith303`,
`SecondAnswerFromTheSameBrowserReplacesTheFirst`, `AnswerOverMaxAnswersIs409`, `AskerSeesTheAnswerWithNameAndNote`, `HiddenAnswerLeavesTheTally`,
`ThirdReportHidesTheAsk`, `StoreLinkCountsAClick`, `AskAnswersJobPushesOncePerAskPerRun`, `AskAnswersJobStopsAtTheMaxPushes`,
`AskAnswersJobSkipsAUserWithThePreferenceOff`, `PurgeDeletesCopiesOfExpiredAsks`, `PurgeDeletesAsksAfterRetention`, `PurgeSweepsOrphanCopies`,
`AskFriendMetricsSqlRuns`, `AskFriendCopyLinesArePinned`.

**Mobile PR M1 (`kombin-frontend`): ask and share.** Regenerated contract (`docs/openapi.json` copied after AF2), the "Arkadaşına sor" button on
the outfit detail and today's outfit card (hidden on 404 `AskFriend.Disabled`), the "+ İkinci kombin" picker (saved and today's outfits), the
consent sheet (server text), create → `Share.share({ message: shareText + "\n" + url })`, error lines for the 403/409s, `ask_create` and `ask_share`.
Tests: button hidden when disabled, consent shown only when `accepted` is false, share opens with the server text, the 409 lines render.

**Mobile PR M2: results.** Contract regenerated after AF3, "Sorduklarım" list, the result view (tally bar, answers with name and note as plain
text, "Gizle", "Linki kapat", "Tekrar paylaş" while open), the push deep link `kombin://asks/{id}`, the "Arkadaş cevapları" toggle in notification
settings, `ask_view`, `ask_close`, `ask_answer_hide`. Tests: deep link opens the ask, hide removes the answer and updates the tally, closed asks show
no share button, the toggle sends `askAnswers`.

## 10. Open questions for the owner (each with the default implementation uses)

1. **In-app asking of people you follow** (push to a chosen friend, an inbox): default **not in v1**; v2 once the follow graph exists, mutual
   follows only.
2. **The link's address**: default `https://api.<domain>/sor/{token}` (`PublicBaseUrl`). A shorter `kombin.<tld>/sor/...` needs the apex served by
   the API instead of GitHub Pages; not before launch.
3. **The window**: default 24 h. Shorter (12 h) shows the pictures for less time; changing it means a new consent version.
4. **Should the friend see the tally after answering?** Default yes (counts only, no names or notes).
5. **Counsel**: the consent text and storing friends' optional names and notes (people without an account, possibly minors) for 30 days: default as
   above, listed as privacy question 19; nothing ships enabled before the owner switches `AskFriend:Enabled`.
6. **Shopping on the page** (prices, "where is it cheapest" for catalog pieces): default none in v1; the page's only call to action is the app.
