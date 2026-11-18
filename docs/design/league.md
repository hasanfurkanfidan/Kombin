# Kombin Ligi: design (task L1)

Source of the rules: `Tasarım/kombin-ligi-aritmetik.md` (16.11.2026, the founder's decision, updated the same day: only a worn outfit
competes, aliases in the table, 18+, the win rate after a vote; quoted as "the rules" below, section numbers are its own). Screens
`Tasarım/kombin-ligi/01-05` are context only. This document changes no rule. The owner's answers to the open questions of the first draft
(task L1) are written into the sections they belong to; one question remains [open](#open-questions).

Status: accepted (task L2). PR 1 (foundations, task L2), PR 2 (entering and voting, task L3), PR 3 (day close, points, results, task L4) and PR 4
(weeks, aliases, style score, reviews, the push, the metrics, task L5) are implemented. "Not verified" marks a statement about behaviour I
did not read in the code.

---

## 1. Goal and scope

**Goal.** "Bugünkü kombinin yarışır, sen de on çifte oy verirsin, akşam kaçıncı olduğunu görürsün; hafta sonunda ligdeki yerin belli olur."
The league is a daily, anonymous, pairwise vote on the outfits players actually chose or wore that day, ranked at 21:00 (Europe/Istanbul) with
Bradley–Terry, turned into daily points (votes up to 70, usage up to 30) and weekly leagues (Bronz → Elmas, groups of about 30).

**In v1:**

- joining once with consent (which is also the 18+ declaration), leaving, withdrawing the day's outfit;
- the day's **worn** outfit entering by itself, with the repeat rule (30 days exact, 7 days one piece apart); saving never enters;
- voting on silent pairs, 30 a day, with the speed and new-account rules and "karar veremedim"; after a vote, the chosen outfit's real win
  rate today;
- a per-week alias for every group member ("Yeşil Trençkot") instead of a name;
- engine outfits filling a small pool (never ranked or listed);
- the 21:00 day close (Bradley–Terry, percentile, daily points), the Sunday 21:00 week close (best 5 of 7, promotion, relegation, groups);
- reports on an outfit, the review of reported outfits through an internal endpoint;
- the style score and creator eligibility, computed and stored, with no screen and no endpoint;
- one push notification: the evening result (the rules list "sonuç dışında bildirim" as not in v1, so the result itself is in).

**Not in v1** (rules, section 7), and the backend adds no table, column or endpoint for them: günlük görev, bütçe modu, hakem (isabet) puanı,
seri ve rozetler, arkadaş tablosu, ayna fotoğrafı günü, "makineyi yendin" mesajı, kazanan kombinden kazanç payı, sonuç dışında bildirim.
Also not in v1 because nothing asks for it: a separate outfit builder (the rules: "Ayrıca kombin kurma ekranı yok"), comments, people photos,
searching for an outfit, real names in the
group table (section 4), an estimate of how many voters "agree" (section 7), occasion or audience labels on a pair (the rules: the pairing is
silent).

**Off by default.** `League:Enabled` is **false**: every league route answers 404 and no league job is registered until configuration
switches it on when the app screens ship. Tests enable it explicitly.

---

## 2. Slice placement

**A new slice `Features/League`, schema `league`.** The league has its own tables, jobs and rules, and none of the existing slices owns "a daily
competition among strangers", so putting it inside Recommendation or Social would mix two products in one folder.

```
src/Kombin.Api/Features/League/
  LeagueModule.cs                  MapLeagueEndpoints(), AddLeagueFeature(), UseLeagueJobs()
  LeagueOptions.cs                 every number of this document (League:*), with a validator
  LeagueErrors.cs
  LeagueConsent.cs                 versioned consent texts, pinned like PostingConsent
  Rules/                           pure: RepeatRule, Pairing, WinRate, BradleyTerry, DailyPoints, WeeklyTable, StyleScore, LeagueAliases
  TodaysOutfit.cs                  which outfit is "today's", its piece set (I/O)
  EntryMaterializer.cs             entries from outfits, safety check, image copies (I/O)
  GetMe/ Join/ Leave/ WithdrawEntry/ NextPair/ Vote/ Report/ GetResults/ GetTable/   one Endpoint.cs (+ Validator.cs) each
  Internal/ReviewQueue/ Internal/Review/ Internal/Close/
  Jobs/LeagueEntriesJob.cs  Jobs/LeagueFillJob.cs  Jobs/LeagueCloseJob.cs  Jobs/LeaguePurgeJob.cs
src/Kombin.Api/Persistence/Entities/League/*.cs
src/Kombin.Api/Persistence/Configurations/League/*.cs
```

**What it touches, and how.** Reading another feature's tables through `KombinDbContext` is not a feature reference (entities are shared,
`Persistence/Entities`), so most of the league reads data and calls no code:

| Existing part | What the league needs | Through | Boundary effect |
|---|---|---|---|
| Recommendation outfits (`Persistence/Entities/Recommendation/Outfit.cs`, `OutfitItem.cs`, `OutfitFeedback.cs`) | today's worn outfit, its pieces, the "worn" event | reads the entities | none |
| Wardrobe items (`Persistence/Entities/Wardrobe/WardrobeItem.cs`) | piece images, archive state, the "piece added" event | reads the entities | none |
| Wardrobe image safety (`Features/Wardrobe/WardrobeModerator.cs`: `IsShareable`, `ModerateAsync`) | only `Allow` pieces may be shown to strangers; ask again when a verdict is missing | **Seam** `WardrobeModerator`, the same seam Social already uses (`tests/Kombin.Api.Tests/FeatureBoundaryAllowList.cs:83`) | +1 reference |
| The outfit engine (`Features/Recommendation/OutfitGenerator.cs`, `OutfitSources.cs`) | engine outfits for a small pool | **Seam** a new `CatalogOutfitSampler` owned by Recommendation (below) | +2 references (`CatalogOutfitSampler`, `SampledOutfit`) |
| Profile (`Persistence/Entities/Profile/UserProfile.cs`: `Audience`, `BirthYear`) | the outfit's audience, the age check | reads the entity | none |
| Identity (`Persistence/Entities/Identity/User.cs`: `CreatedAt`, `DisabledAt`, `DeletionRequestedAt`) | trusted reporters, hiding disabled and pending-deletion players | reads the entity | none |
| Identity's purge (`Features/Identity/AccountPurger.cs`) | the league's copied images are deleted with the account | `AccountPurger.ObjectKeysAsync` also reads `league.entry_pieces.image_key` (an entity) | none (Identity reads an entity) |
| Notification (`Features/Notification`) | the evening result push | Notification's own job reads `league.daily_scores` (an entity) and writes `Notification` rows, like `alert-dispatcher` reads `engagement.alert_triggers` | none |
| Europe/Istanbul and the Hangfire flag (today `QuietHours.Zone`, `NotificationModule.HangfireEnabledKey` in `Features/Notification`) | every job and day boundary | **moved to `Common`** first (below) | −12 references |

**The engine seam.** "Az oyunculu günlerde havuzu öneri motorunun kombinleri doldurur" needs the engine's behaviour, and copying the generator
is forbidden (Budget and trip rules: "Never fork them"). Recommendation gets one narrow static class:

```csharp
// Features/Recommendation/CatalogOutfitSampler.cs (owned and documented by Recommendation)
internal sealed record SampledOutfit(IReadOnlyList<(OutfitSlot Slot, Guid ProductId, string ImageUrl)> Pieces, int Score);
internal static class CatalogOutfitSampler
{
    // Catalog-only outfits (no wardrobe), built by OutfitGenerator through the narrow per-slot prefetch, at or above the acceptance floor.
    public static Task<IReadOnlyList<SampledOutfit>> SampleAsync(KombinDbContext db, Audience audience, Occasion? occasion,
        DateOnly date, int count, int seed, CancellationToken cancellationToken);
}
```

Catalog-only, because an engine outfit built from another user's wardrobe would show that user's pieces to strangers without their consent.

**FeatureBoundaryTests.** The allow-list holds 141 references in 37 pairs (`tests/Kombin.Api.Tests/FeatureBoundaryAllowList.cs`, header). The
league needs 3 new `Seam` references in 2 new pairs:

```
new("League", "Wardrobe", Kind.Seam, "League entries show wardrobe pieces to strangers only through the image safety filter (Wardrobe rules, IsShareable; League rules).", "WardrobeModerator"),
new("League", "Recommendation", Kind.Seam, "Engine outfits fill a small pool; they come from the outfit engine, never a copy (League rules).", "CatalogOutfitSampler", "SampledOutfit"),
```

Both are real seams (the league must call another feature's behaviour), so rule 2 of "Feature boundaries" applies; no `Legacy` line is added.
The league also needs the Istanbul zone and the Hangfire flag, which today live in Notification and are `Legacy` everywhere they are used
(`HangfireFlagAndZone`, allow-list line 14). Naming them from League would be a new `Legacy` line, which is forbidden, so **PR 1 moves them to
`Common`** (rule 1): `Common/Time/Istanbul.cs` (`ZoneId`, `Zone`, `Today(now)`) and `Common/Hosting/HangfireSettings.cs` (`EnabledKey`), and
updates every caller. That deletes the four whole pairs Admin/Analytics/Catalog/Identity → Notification (8 references) and `NotificationModule`
and `QuietHours` from the Social and Wardrobe → Notification lines (4 references). **Net: 141 − 12 + 3 = 132 references in 37 − 4 + 2 = 35
pairs.** The count does not grow. (As built: 132 references in 36 pairs; the list held 34 pairs after PR 1, not 33.) `TheAllowListHoldsNoReferenceThatNoLongerExists` will name the lines to delete.

No new cycle: neither Wardrobe nor Recommendation will ever reference League (the league pulls, nothing pushes into it).

---

## 3. Data model

Schema `league`, snake_case by the global convention. Every `user_id` has a foreign key to `identity.users` with `ON DELETE CASCADE` unless
said otherwise. Instants are `timestamptz` (UTC); a league day is a `date` in Europe/Istanbul. Enums are stored as their names (`text`, at most 30
characters), like every other schema here. Built in PR 1 as migration `League` (14 tables).

### 3.1 Tables

**`league.players`**: one row per user who ever joined.

| Column | Type | Note |
|---|---|---|
| `user_id` | uuid PK | |
| `tier` | text (enum name) | `LeagueTier`: Bronze, Silver, Gold, Platinum, Diamond (Bronz, Gümüş, Altın, Platin, Elmas); Bronze on join |
| `joined_at` | timestamptz | the latest join |
| `left_at` | timestamptz null | set by leave, cleared by a new join |
| `consent_version` | text | the version accepted at the latest join |
| `counted_votes` | int | lifetime counted votes; decides the half weight of the first 20 |
| `idle_weeks` | smallint | consecutive closed weeks without a competed day; reset by a competed day and after an idle relegation |

**`league.consents`** (append-only): `id` bigint PK, `user_id`, `action` (`Join`, `Leave`), `version` text null, `text` text null (the exact
text shown, as `social.posting_consents` stores it), `at`. It is the join/leave record; `players` holds only the current state.

**`league.days`**: `day` date PK, `closes_at` timestamptz (21:00 Istanbul of that day), `closed_at` null, `ranked_count` int, `engine_count`,
`pair_count`, `bt_iterations`, `bt_converged` bool, `close_ms` int. A missing row means "open, nothing closed"; so does a row with `closed_at`
null (the first vote, a withdrawal or a leave creates it, so the close's `FOR UPDATE` has a row to wait on).

**`league.voter_days`** (task L4): `user_id`, `day` (PK), `served`, `counted`, cascade on the user. The voter's own counters of the day: the
pairs served (the daily limit and the next sequence come from it, moved in the serving transaction) and the counted votes (the voting points come
from it, moved in the vote's transaction). Pair rows are deleted with the account of the player whose outfit they showed; these counters are not,
so "voted on ten pairs" and the 30-pair cap never shrink because someone else left. Deleted by `league-purge` 30 days after a closed day.

**`league.entries`**: one outfit in one day's pool.

| Column | Type | Note |
|---|---|---|
| `id` | uuid PK | never shown to anyone but the owner |
| `day` | date | |
| `user_id` | uuid null | null for an engine outfit |
| `kind` | text (enum name) | `Human`, `Engine` |
| `outfit_id` | uuid null | the source `recommendation.outfits` row, `ON DELETE SET NULL` |
| `audience` | text (enum name) | `Audience` (Women, Men, Unisex) |
| `occasion` | text null (enum name) | `Occasion` (Office, Weekend, Evening, Sport, Event); null is its own bucket |
| `piece_ids` | uuid[] | sorted wardrobe item IDs of the non-accessory pieces (product IDs for engine) |
| `piece_set_hash` | text | SHA-256 hex (32) of `piece_ids` joined, like `OutfitHash.Of` (`Features/Recommendation/OutfitGenerator.cs:42`) |
| `status` | text (enum name) | `Pending` (waiting for the safety check), `Competing`, `NotCompeting`, `Withdrawn`, `Removed` |
| `reason` | text null (enum name) | for `NotCompeting`: `RecentlyCompeted`, `UnsafePiece`, `IncompleteOutfit`, `PieceUnavailable`, `AgeRestricted` |
| `exposures` | int | pairs served with this entry |
| `comparisons` | int | counted votes on this entry (unweighted; the "8 comparisons" threshold) |
| `weighted_wins`, `weighted_games` | real | running sums of counted votes; the win rate shown after a vote (section 7) |
| `counting_reports` | int | trusted reports since the last review |
| `locked_at` | timestamptz null | first exposure; after it the entry no longer follows the player's outfit changes |
| `strength`, `percentile` | double null | written by the day close |
| `rank` | int null | 1 = best, among ranked human entries |
| `reviewed_at` | timestamptz null | |
| `created_at`, `updated_at` | timestamptz | |

Indexes: **unique `(day, user_id) WHERE user_id IS NOT NULL`** (one outfit per player per day); `(day, status, audience, occasion, exposures)`
for pairing; `(user_id, day)` for the repeat rule; `(day) WHERE kind = Engine`.

**`league.entry_pieces`**: `entry_id` (cascade), `position` smallint, `slot` (`OutfitSlot`), `wardrobe_item_id` uuid null (`ON DELETE SET NULL`),
`product_id` uuid null, `image_key` text null (our copy, section 4), `image_url` text null (catalog image for engine pieces). PK `(entry_id, position)`.
Accessories are stored and shown; they are only left out of `piece_ids`.

**`league.pairs`**: one served pair, and the vote on it.

| Column | Type | Note |
|---|---|---|
| `id` | uuid PK | random; the only identifier the voter sees |
| `day` | date | |
| `voter_id` | uuid | |
| `left_entry_id`, `right_entry_id` | uuid | cascade from `entries` |
| `low_entry_id`, `high_entry_id` | uuid | the two IDs ordered, for the uniqueness below |
| `match_level` | text (enum name) | `Strict` (same occasion and audience), `SameAudience`, `Any` |
| `sequence` | smallint | 1..30 within the voter's day |
| `served_at` | timestamptz | |
| `answered_at` | timestamptz null | |
| `choice` | text null (enum name) | `Left`, `Right`, `Skip` |
| `client_decision_ms` | int null | what the app measured from display to tap |
| `counted` | bool | |
| `weight` | real | 1, or 0.5 for the voter's first 20 counted votes; 0 when not counted |

Indexes: **unique `(voter_id, day, low_entry_id, high_entry_id)`** (the same pair is never shown twice to the same player, and so one vote per
player per pair); **unique `(voter_id, day, sequence)`** (the 30-pair cap cannot be raced); `(day) INCLUDE (left_entry_id, right_entry_id, choice, weight)`
for the close; **partial unique `(voter_id, day) WHERE answered_at IS NULL`** (at most one open pair per voter).

**`league.reports`**: `entry_id` (cascade), `reporter_id`, `reason` text null (`Inappropriate`, `NotClothing`, `Other`), `counts` bool (trusted
reporter, decided at report time), `created_at`. PK `(entry_id, reporter_id)` (one report per player and outfit).

**`league.daily_scores`**: one row per player and closed day with any points or an entry.

| Column | Note |
|---|---|
| `user_id`, `day` | PK |
| `entry_id` | null when the player had no competing entry |
| `competed` | the entry was `Competing` at the close (ranked or not) |
| `ranked` | at least 8 comparisons |
| `rank`, `ranked_count`, `comparisons`, `percentile` | the screen-04 numbers |
| `vote_points` (0-70), `voting_points` (0/15), `worn_points` (0/10), `piece_points` (0/5), `total` (0-100) | integers |
| `notified_at` | set by Notification's result push |

Index `(day)`, and the PK serves the week sums.

**`league.weeks`**: `week_start` date PK (a Monday), `ends_at` (Sunday 21:00), `closed_at` null, `groups_formed_at` null.

**`league.groups`**: `id` uuid PK, `week_start`, `tiers` text[] (tier names) (one tier, or two or more when merged), `capacity` int.

**`league.group_members`**: `week_start`, `user_id`, `group_id`, `alias` text (section 4), `tier` (the player's own label, kept in a merged
group), `week_points` int null, `competed_days` smallint null, `final_rank` int null, `outcome` text null (`Promoted`, `Relegated`, `Stayed`,
`IdleRelegated`), `joined_at`. PK `(week_start, user_id)` (one group per player per week); **unique `(group_id, alias)`** (an alias is unique
within its group).

**`league.style_scores`**: `user_id` PK, `computed_at`, `counted_days` int, `mean_p`, `sd_p`, `score` double null, `qualifying_weeks` smallint,
`league_eligible_at` timestamptz null, `revoked_at` null, `revoked_reason` text null.

**`league.engine_preferences`** (no user ID, by construction): `id` bigint, `day`, `audience`, `occasion`, `winner_products` uuid[],
`loser_products` uuid[], `weight` real. Written by the day close for every counted vote that involved an engine outfit ("Oylar motor için tercih
verisi olarak saklanır"); it survives account deletion because it holds nothing about a person.

**`league.state`**: key/value for the jobs (`engine_fill_at:{day}`), so no job keeps state in memory.

### 3.2 Why these shapes

- Pairs and votes are one row, because a vote only exists for a served pair and the uniqueness of the pair is also the uniqueness of the vote.
- `piece_ids` is an array on the entry, not a join: the 7-day rule reads at most 7 rows per player and compares sets in memory.
- Daily scores are stored, not recomputed, so a closed day never changes when an account is deleted or a rule setting changes later.

---

## 4. Consent and privacy

**Join.** The first league screen (`GET /league/me` says `joined: false`) shows the consent; `POST /league/join { consentVersion }` records it.
Consent texts are versioned and append-only like `PostingConsent` (`Features/Social/PostingConsent.cs`), with their hashes pinned in a test
(`LeagueConsentTests`). Version `2026-11-v1` was the owner's first text, the card on screen 01. The current version is **`2026-11-v2`** (task
L3-fix, the owner's text), which adds one sentence, because the image copies are still the same pictures and we do not pretend otherwise:

> Katılırsan dolabındaki parçaların fotoğrafları diğer oyunculara gösterilir. Adın ve profilin görünmez. Parçalarını tanıyan biri kombinin
> sana ait olduğunu anlayabilir. İstediğin gün kombinini çıkarabilir, ayarlardan ligden ayrılabilirsin. Katılarak 18 yaşından büyük olduğunu
> onaylarsın.

Every player who joined under version 1 sees `consentRequired: true` and enters no outfit until they accept version 2. Its last sentence is the age declaration (section "Minors" below). Counsel has not reviewed the text yet (the [open question](#open-questions);
`docs/privacy-notes.md` says so). A new version makes `GET /league/me` answer `consentRequired: true` and stops new entries until accepted; votes
and the table keep working.

**Leave.** `POST /league/leave` sets `players.left_at`, writes a `Leave` row, withdraws today's entry (its image copies are deleted by the next
`league-entries` run, within minutes; see section 13) and takes the player off the current week's group table. History stays for the player (and is deleted with the
account). A new join keeps the old tier.

**Withdraw today's outfit.** `DELETE /league/me/entry` sets the entry `Withdrawn`. Final for the day, so a player cannot re-roll an entry.
Its comparisons stay in the Bradley–Terry fit as information about the other outfits; it is not ranked and earns no vote points.

**Anonymity** means: **no name, no profile, no stable identifier in any response**. No league response ever contains a user ID, a display
name, a handle, an entry ID of someone else's outfit, a wardrobe item ID, or any value that is the same for one person's outfits on two days. It
does not mean that an outfit cannot be traced to its owner: the pieces are the owner's photos, and someone who knows them (or compares the pixels)
can tell whose outfit it is; consent version 2 says so.

- voters see a pair ID (random per serve) and, per side, a list of `{ slot, imageUrl }`; nothing else;
- **images are copied**, because the stored keys contain the owner: `wardrobe/{userId}/{itemId}/thumb.webp` (`Common/Storage/StorageKeys.cs`,
  `StorageKeys.Build`). When an entry becomes `Competing`, each piece's thumbnail is copied to
  `StorageKeys.Build("league", <random guid>, <random guid>, ImageVariant.Thumb, ext)`; the name `thumb.*` makes it public under the existing
  bucket policy (`docker/minio/public-read.json`), and the random segments mean the URL holds no user ID and is not the same for the same piece on another day.
  The file is not the source either (task L3-fix): `ImageMarker` (`Common/Storage`) adds 16 to 271 random bytes where decoders skip them, so the
  SHA-256, the ETag and the size of every copy are its own; the pixels are untouched, so a perceptual hash or a person who knows the piece still
  matches them (`docs/privacy-notes.md` 6b says what that leaves; consent version 2 tells the player).
  A catalog-sourced wardrobe item without a stored thumbnail uses its product's primary image URL (a retailer URL, linked to no one).
- the group table rows are `{ rank, points, isMe, alias }`. **No real names**: the consent says "Adın ve profilin görünmez", and the display
  name falls back to the e-mail local part for some accounts (Identity rules). Each group member gets a **per-week alias** "<colour> <garment>"
  in Turkish ("Yeşil Trençkot", "Lacivert Blazer"), generated when the player is placed in the week's group, unique within the group
  (`unique (group_id, alias)`), new every week (never the player's alias of the previous week), and **never derived from the display name, the e-mail or the user ID**: it is drawn from the
  word lists with a cryptographic random number, not a hash of the user, so it cannot be recomputed by anyone to link two weeks. The word lists
  live in code (`LeagueAliases.Colours`, `LeagueAliases.Garments`: everyday colour words and garment names as the app glossary wants them, at
  least 15 × 15 = 225 combinations so a group of 35 always finds a free one), pinned by `LeagueAliasTests` and scanned by `CopyGlossaryTests`.
  The caller's own row says `isMe: true` and the app shows "Sen" instead of the alias.
- the owner sees their own entry, rank, comparisons and points; never who voted, never the opponents.

**Account deletion** (Identity rules; `docs/privacy-notes.md` section 1a gets a `league` block):

| Table | What happens |
|---|---|
| `league.players`, `consents`, `daily_scores`, `group_members`, `style_scores` | deleted (cascade on `user_id`) |
| `league.entries` (the user's) and their `entry_pieces` | deleted (cascade); the copied images are deleted first by `AccountPurger` (objects first, as today) |
| `league.pairs` | the user's votes: deleted (cascade on `voter_id`); pairs that showed the user's entries: deleted (cascade on the entry) |
| `league.reports` | the user's reports and the reports on the user's entries: deleted (cascade) |
| `league.engine_preferences` | kept: holds no user ID |
| `league.days`, `weeks`, `groups` | kept: no personal data |

Closed days and weeks of other players do not change (their scores are stored). While deletion is pending (`users.deletion_requested_at`) or
the account is disabled (`users.disabled_at`), the player's entry is left out of pairing and of the close, like posts move to `AccountPending`.

`AccountDeletionTests` (matrix at `tests/Kombin.Api.IntegrationTests/AccountDeletionTests.cs:586`) gets these lines: `league.players`,
`league.consents`, `league.entries`, `league.entry_pieces` (through the user's entries), `league.pairs` (`voter_id` = user, or an entry of the
user), `league.reports` (`reporter_id` = user, or an entry of the user), `league.daily_scores`, `league.group_members`, `league.style_scores`,
and a check that the user's league image copies are gone from `FakeObjectStorage` and that another player's `daily_scores` row is unchanged.

**Minors.** The rules: "Katılım 18 yaş ve üstü içindir: rıza metni bunu söyler, doğum yılı 18'den küçük gösteren hesap katılamaz."
`UserProfile.BirthYear` (`Persistence/Entities/Profile/UserProfile.cs:15`) is optional. The rule is **strict** (task L3, the owner's
correction of PR 1): a known birth year blocks unless the player is certainly of age, `year − birthYear <= League:MinAge` (18) in the current
Istanbul year. A year alone cannot say whether the birthday has passed, so someone born in 2008 waits until 2027 even if they turned 18 in
2026; that is the accepted cost. Such a player cannot join (403 `League.AgeRestricted`), and no entry is created if such a year is set later
(`AgeRestricted`), by the same `LeagueRules.IsUnderAge`. An unknown birth year may join: the consent
sentence "Katılarak 18 yaşından büyük olduğunu onaylarsın" is the declaration, and there is no separate age gate.

**Records for counsel.** `docs/privacy-notes.md` gets a section "League": the consent (and that counsel has not reviewed it), the 18+
declaration, the copied images and their 7-day life, the aliases, the vote rows (30 days), the engine preference rows (no user), the style score
(no screen). `docs/security-review.md` lists the new surface.

**No Metabase access.** `metabase_ro` stays limited to `analytics`, `engagement` and `catalog` (`docker/postgres/metabase_ro.sql`); the league
schema holds user IDs and is never granted to it. The first-month queries live in `docs/metrics.sql` and are run by hand (section 13).

---

## 5. Entering the league

**Which outfit is "today's".** The rules: "Oyuncunun o gün giydiği kombin yarışır: 21:00'den önce 'Bunu giyiyorum' / 'Bugün giydim' diye
işaretlediği kombin (16.11 kararı: kaydetmek yarışa sokmaz, yalnızca giymek sokar)." In the code, both buttons are
`POST /me/outfits/{id}/worn` (`Features/Recommendation/WearOutfit/Endpoint.cs`), which runs `OutfitActions.WearAsync`
(`Features/Recommendation/OutfitActions.cs`): it sets `outfits.status = Worn`, `worn_at`, and appends an `outfit_feedback` row with
`action = Worn`. **Saving (`POST /me/outfits/{id}/save`) never enters the league.**

The day's outfit for player U on day D (`TodaysOutfit.ResolveAsync`): the outfit with the latest `worn_at` inside D (Istanbul) and before 21:00,
any kind. Nothing else qualifies.

**When it enters.** Nothing in Recommendation calls the league (no reverse reference, no new cycle). Instead:

- `league-entries` (every minute) reads `recommendation.outfit_feedback` rows with `action = Worn` and `created_at >= now − 10 min`
  for players who are joined, and resolves each such player's day outfit. The 10-minute look-back makes it idempotent and tolerant of a missed
  minute; a watermark on the bigint ID was rejected because IDs are not committed in order. PR 2 adds an index on `outfit_feedback (created_at)`
  (only `(user_id, created_at)` exists, `Persistence/Configurations/Recommendation/OutfitFeedbackConfiguration.cs:20`).
- `GET /league/me` resolves the caller at once, so the owner never waits for the job to see their status.

An entry follows the player's latest worn outfit until it is first served (`locked_at`); after that it is fixed for the day (the rules: "ilk
kez bir oylamada gösterildikten sonra o gün değişmez"), because changing an outfit voters already judged would make their votes about something
else. The "worn" points do not depend on which outfit competes (section 9).

**Eligibility, in order** (the first failure is the `reason`):

1. Age: `AgeRestricted` (section 4).
2. Every piece is a wardrobe piece and there are at least `League:MinPieces` (2) non-accessory pieces: else `IncompleteOutfit`. A `Gap` piece or a catalog `Anchor`
   (`OutfitItemRole`, `Persistence/Entities/Recommendation/RecommendationEnums.cs`) is something the player does not own, and "parça fotoğrafları"
   are wardrobe photos.
3. No piece is archived: else `PieceUnavailable`.
4. Repeat rule: else `RecentlyCompeted`.
5. Safety: every piece `WardrobeModerator.IsShareable` (`Features/Wardrobe/WardrobeModerator.cs:22`). A piece with no verdict yet (the call
   failed) keeps the entry `Pending`; `league-entries` asks `WardrobeModerator.ModerateAsync` for up to `League:ModerationRetriesPerRun` (50) such
   pieces per run, rather than waiting for `wardrobe-cleanup`'s daily retry. A `Review` or `Block` verdict makes the entry `NotCompeting` with
   `UnsafePiece`; the owner is not told which piece (the wardrobe never shows verdicts either). An entry still `Pending` at 21:00 does not compete.
6. Otherwise `Competing`: the thumbnails are copied (section 4) and the entry joins the pool.

**The repeat rule** (rules, section 1; pure `RepeatRule`, unit-tested):

- piece set = the sorted identities of the non-accessory pieces. Accessories are left out exactly as `OutfitHash.Of` leaves them out
  ("they never decide an outfit"), so the league and the engine agree on what "the same outfit" is. A piece's identity is its wardrobe item ID,
  except for a piece added from the catalog (`wardrobe_items.from_catalog` with a product), whose identity is the **product ID** (task L3-fix,
  `RepeatRule.Identity`): archiving such a piece and adding the same product again makes a new wardrobe item, not a new piece. A photo piece
  photographed again is a new item and a new piece (nothing can tell);
- **exact**: blocked if the player has an entry with the same `piece_set_hash` that *competed* on a day in `[D − 29, D − 1]`
  (`League:RepeatExactDays` = 30, "30 gün içinde yalnızca bir kez");
- **near**: blocked if a competed entry on a day in `[D − 6, D − 1]` (`League:RepeatNearDays` = 7) has `max(|A \ B|, |B \ A|) ≤ 1`
  (`League:RepeatNearMaxDifference` = 1): one piece swapped, added or removed;
- *competed* = the entry was `Competing` at its day's close (`daily_scores.competed`). Withdrawn, removed or not-competing entries block nothing.

```sql
-- exact (uses (user_id, day)); the near check loads the same rows for the 7-day window and compares piece_ids in memory
SELECT e.piece_set_hash, e.piece_ids, e.day
FROM league.entries e JOIN league.daily_scores s ON s.user_id = e.user_id AND s.day = e.day AND s.entry_id = e.id
WHERE e.user_id = @user AND e.day BETWEEN @d - 29 AND @d - 1 AND s.competed;
```

On such a day the app shows the rules' line: **"Bu kombin yakın zamanda yarıştı, bugün yarışmıyor."** The "worn" points are still given; no vote
points; the outfit is not in the pool.

---

## 6. Pairing

`GET /league/pairs/next` (pure `Pairing.Choose` over a candidate list; the I/O is one query plus the voter's pairs of the day).

**Before choosing.** If the voter has an unanswered pair today, it is returned again: re-asking never re-rolls, so no one can fish for a
particular outfit ("belirli bir kombini arayıp bulamaz"). If the voter has been served `League:MaxPairsPerDay` (30) pairs, the answer is
`{ pair: null, reason: "DailyLimit" }` ("bugünlük bitti"). Skips and too-fast votes count toward the 30: the cap limits what one person sees.

**Candidates.**

```sql
SELECT id, kind, audience, occasion, exposures FROM league.entries e
WHERE e.day = @day AND e.status = 1 /* Competing */ AND e.counting_reports < @hideAt
  AND (e.user_id IS NULL OR e.user_id <> @voter)
  AND NOT EXISTS (SELECT 1 FROM league.reports r WHERE r.entry_id = e.id AND r.reporter_id = @voter)
  AND (e.user_id IS NULL OR NOT EXISTS (SELECT 1 FROM identity.users u WHERE u.id = e.user_id
        AND (u.disabled_at IS NOT NULL OR u.deletion_requested_at IS NOT NULL)))
  AND (e.user_id IS NULL OR EXISTS (SELECT 1 FROM league.players p WHERE p.user_id = e.user_id AND p.left_at IS NULL))
  AND e.kind = @kind     -- run twice: Human, then Engine
ORDER BY e.exposures, e.id
LIMIT @candidateLimit;   -- League:PairCandidates = 200 of each kind
```

The candidates are **two lists** (task L3-fix): the 200 least-exposed human entries and the 200 least-exposed engine entries. One list of 200 let
the engine fill crowd every human entry out once the human ones had been shown once (12 buckets × 3 players leave 324 engine entries at
exposure 0), and then no A existed and every request answered `NoPairs`. Two engine entries are still never paired (A is always human). Each list
is read through the index `(day, status, kind, exposures, id)` (migration `LeaguePairCandidates`), so it stops after its 200 rows instead of
sorting the day's pool.

**Choosing.**

1. **A** = the human candidate with the fewest exposures; ties broken by a seeded hash of `(voter, day, sequence, entryId)`, so it is
   deterministic in tests and spread across voters. A is always human: a pair of two engine outfits teaches the ranking nothing.
2. **B** = the candidate with the fewest exposures, B ≠ A, the pair (A, B) not yet served to this voter, at the first level that has one:
   1. `Strict`: same `occasion` (null matches only null) and same `audience`;
   2. `SameAudience`: the occasion condition dropped ("önce yer");
   3. `Any`: the audience condition dropped too ("sonra kitle").

   B may be human or engine.
3. If no A has a B at any level, A is the next human candidate; if none works, `{ pair: null, reason: "NoPairs" }`.
4. In one transaction: insert the pair (`sequence` = the voter's last sequence of the day + 1, and the daily limit is decided from that same
   number, so a pair deleted with another player's account leaves a gap and is never reused; the unique indexes stop a double tap and a parallel
   request) and `UPDATE entries SET exposures = exposures + 1, locked_at = coalesce(locked_at, now)` for both, the two rows locked in ID order
   with `FOR NO KEY UPDATE` (the vote does the same, so serves and votes on the same two outfits wait instead of deadlocking; `FOR UPDATE` would
   conflict with the key-share locks the new pair rows' foreign keys take). Left/right order is a coin from the same hash.

"Least compared" is measured by **exposures**, not finished votes, because concurrent voters would otherwise all receive the same least-voted
outfit until someone answers.

**Engine outfits in the pool** (`league-fill`, every 15 minutes from 00:00 to 20:45): for each audience (Women, Men) and occasion bucket that
has a `Competing` human entry **today** (task L3-fix; "or yesterday" filled buckets ahead of any human entry, but every pair needs a human A, so
those engine entries could only crowd the candidate lists; a new bucket is filled by the next run, within 15 minutes), if `Competing` human entries are fewer than `League:EngineFill:MinPool` (30), add engine entries up to that
size, at most `League:EngineFill:MaxPerRun` (20) per bucket per run, from `CatalogOutfitSampler.SampleAsync` (seed = day number + bucket). Engine
entries pair like any other, are in the Bradley–Terry fit, and are never ranked, counted in N, or listed. The 30 matches the smallest simulated
day of the rules (section 3).

---

## 7. Vote intake

`POST /league/pairs/{pairId}/vote { choice: "left" | "right" | "skip", decisionMs }`.

| Concern | Rule |
|---|---|
| Ownership | the pair's `voter_id` must be the caller; otherwise 404 `League.PairNotFound` (never reveal that it exists) |
| Day open | the day row is read `FOR SHARE`; `now ≥ closes_at` or `closed_at` set → 409 `League.DayClosed`. The close takes the row `FOR UPDATE`, so a vote and the close are serialised |
| Idempotency | `UPDATE pairs SET ... WHERE id = @id AND answered_at IS NULL`; a repeat with the same choice returns the stored result (200); a different choice is 409 `League.AlreadyVoted` |
| Speed | counted only if **both** `answered_at − served_at ≥ League:MinDecisionTime` (0.6 s) and `decisionMs ≥ 600`. The server time alone is lenient (it includes image loading, and a prefetched pair is served earlier than shown); the app's time alone can be forged. Not counted = stored with `counted = false`, `weight = 0` |
| Half weight | `weight = League:NewVoterWeight` (0.5) while `players.counted_votes < League:NewVoterVotes` (20), else 1; `counted_votes` is incremented in the same transaction, so the 21st counted vote is the first at full weight |
| Daily limit | enforced at serve time (section 6), so the vote needs no second check |
| "Karar veremedim" | `choice = skip`: stored, never counted, no weight, no win rate; it uses up the pair |
| Entry counters | for a counted vote: `comparisons + 1` on both entries; `weighted_games + weight` on both, `weighted_wins + weight` on the chosen one |

**After the vote: the chosen outfit's real record today.** The rules: "Her oydan sonra seçilen kombinin o günkü gerçek galibiyet oranı görünür
('%72 bugünkü galibiyet oranı'; oydan önce değil; kombin 5'ten az karşılaşma yaptıysa sayı yerine 'İlk oylardan biri seninki')." No estimate of
how many voters "agree" is computed or returned: pairs rarely repeat across voters, so such a figure would be invented. The response carries
the chosen entry's own counters, **including this vote** (read in the same transaction, after the update):

```
chosenGames   = weighted_games of the chosen entry (rounded down to an integer for the response)
chosenWinRate = round(100 × weighted_wins / weighted_games), an integer 0-100
                null when weighted_games < League:MinGamesForWinRate (5), and null for a skip
```

It is a fact about today's counted votes, not a rating: it is not stored separately and not used for ranking (the rules forbid an intraday
rating). A too-fast vote changes no counter, so it returns the record as it was. The app shows "%72 bugünkü galibiyet oranı", or
"İlk oylardan biri seninki" when the rate is null.

Response: `{ counted, chosenWinRate, chosenGames, countedToday, servedToday, target: 10, limit: 30 }`.

---

## 8. Day close

**Job `league-close`**: recurring **hourly at :00** in Europe/Istanbul, `[DisableConcurrentExecution(60)]`, `[AutomaticRetry(Attempts = 0)]`,
like the other jobs. Each run closes **every** day whose `closes_at ≤ now` and `closed_at IS NULL` (oldest first), then a due week (section 10),
then the style scores if a week was closed. The 21:00 run does the real work; the other runs are cheap no-ops that catch up after a deploy or a
crash without relying on Hangfire's misfire handling (not verified). Tests call `CloseDayAsync(day, ct)` and `CloseWeekAsync(weekStart, ct)`
directly; they use days in 2020, like the metric tests, because the database is shared.

**As built (task L4)**: `DayCloser` (one day) and `LeagueCloseJob` (`league-close`), with the decisions recorded under PR 3 in section 14.
The job closes, oldest first, every unclosed day of the last `League:CloseCatchUpDays` (7) days whose close has passed, stops at the first
failure (a later day's weekly piece cap reads the earlier days) and logs a warning for an open day older than that window; the internal endpoint
is `POST /internal/league/days/{date}/close`. Tests close their own league days (`NewDay`, far in the future), not days in 2020.

**Lock.** One transaction per day, opened with `SELECT pg_try_advisory_xact_lock(0x4B4F4D42, 2)`: the same class as the embedding lock
(`Features/Catalog/Embeddings/EmbeddingRunLock.cs`, object 1); object 2 is the league close. Transaction-scoped (like
`Features/Wardrobe/AddFromProduct/Endpoint.cs:90`) because the close fits in one transaction, so commit or rollback releases it and a dead
process releases it with its connection. Not getting it = another close is running: log and end successfully. `POST /internal/league/close`
answers 409 `League.CloseInProgress` in that case. Then `SELECT ... FROM league.days WHERE day = @d FOR UPDATE` (created if missing).

**Steps (one transaction):**

1. Load the day's entries (`Competing` or `Withdrawn`, plus engine) and counted pairs (`counted`, weight > 0). Entries of disabled or
   pending-deletion players are left out, and so are their pairs.
2. **Bradley–Terry** (pure `BradleyTerry.Fit`; as built, Newman's 2023 fixed-point iteration applied in place, which has the same fixed point as
   the MM step below and meets the stopping rule: see PR 3 in section 14), minorization–maximization (Hunter 2004) with the virtual prior of the rules ("ortalama bir
   rakibe karşı yarım galibiyet ve yarım yenilgi"): every entry plays one virtual game against a fixed opponent of strength 1, won ½ and lost ½.

   ```
   π_i ← (W_i + ½) / ( Σ_j n_ij / (π_i + π_j)  +  1 / (π_i + 1) )
   ```
   `W_i` = weighted wins, `n_ij` = total weight of i-vs-j votes; start at π = 1; stop when `max_i |ln π_i(new) − ln π_i(old)| < League:BtTolerance`
   (1e-6) or after `League:BtMaxIterations` (500, logged as not converged). The fixed virtual opponent anchors the scale, so no normalisation
   step is needed, and it puts outfits from parts of the pool that never met each other (two audiences) on one comparable scale. Engine and
   withdrawn entries take part as opponents.
3. **Ranking**: human `Competing` entries with `comparisons ≥ League:MinComparisons` (8), sorted by π descending, then by entry ID. N = their
   count. Equal strengths (|Δ ln π| < 1e-9) share a rank (1 + number strictly stronger) and the average position.
4. **Percentile**: the rules' `p = (sıra − 0,5) / N` with *sıra counted from the worst* (worst = 1), which is the only reading that gives
   "1 en iyi"; with `r` the rank from the top, `p = (N − r + 0,5) / N`.
5. **Daily points** for every player (section 9), written to `daily_scores`; `entries.strength/percentile/rank` updated.
6. `engine_preferences` rows for counted votes involving an engine entry; `days.closed_at`, counts, `bt_iterations`, `close_ms`.
7. Commit.

**Same result when re-run.** A closed day is never recomputed (`closed_at` is checked under the row lock). Before the commit nothing is
written, so a re-run starts from the same rows. Votes stop at `closes_at`, so the inputs are fixed; the computation reads entries and pairs
ordered by ID and sums in that order, so the doubles come out the same. The one input that can still move between a crash and the retry is an
account deleted in between, which removes its rows; that is accepted.

**If it dies half-way:** the transaction rolls back and the advisory lock goes with the connection; the next hourly run closes the day.
Results are late by at most an hour; nothing is half-written.

---

## 9. Scoring

One `daily_scores` row per player and closed day (pure `DailyPoints.For`, unit-tested with the rules' example week). All values are settings.

| Part | Points | Derived from | Window |
|---|---|---|---|
| Votes | `round(70 × p)` (away from zero) if ranked; 0 otherwise ("yeterli oy toplanamadı") | the close | the day |
| Voting on 10 pairs | 15 if the player's **counted** votes that day ≥ 10 | `league.pairs` (`voter_id`, `day`, `counted`) | 00:00-21:00 |
| Worn | 10 if the player has an `outfit_feedback` row with `action = Worn` and `created_at` in the day before 21:00 | `recommendation.outfit_feedback` (`OutfitActions.WearAsync` writes it) | 00:00-21:00 (a wear marked after 21:00 earns nothing that day) |
| Piece added | 5 if a wardrobe item of the player was created in the window, is `Ready`, not archived at the close, without the `not_garment` warning; and fewer than 3 earlier days of the same week got piece points | `wardrobe.items` (`created_at`, `status`, `is_archived`, `warnings`) | previous day 21:00 to 21:00 |

Notes:

- "O gün 10 çifte oy vermek": only counted votes count, so ten 0.5-second taps do not earn 15.
- Worn points do not need the worn outfit to be the competing one (the rules: "Oyuncu o kombini yine giyebilir ve 'giydim' puanını (10) alır").
  A wear marked after 21:00 earns nothing that day; the result screen at 21:00 is the nudge.
- Piece additions use a window from 21:00 to 21:00, because evenings are when people photograph clothes, and an evening addition would
  otherwise fall into no day. The window also matches the week (Monday 00:00 to Sunday 21:00: a piece added Sunday 22:00 belongs to Monday).
  `not_garment` items do not count: a photo of a cat is not a piece, and the cap exists against "yüz uydurma parça".
- **Weekly cap of 3** is counted at each close: `SELECT count(*) FROM league.daily_scores WHERE user_id = @u AND day BETWEEN @monday AND @d - 1 AND piece_points > 0`;
  the 4th day with an addition gets 0. The earliest three days win, because a closed day is never changed.
- Usage before joining on the join day counts; a player who left gets no row.
- Vote points are at most `round(70 × (N − 0,5)/N)`: the formula stays as written (owner's decision), so the best outfit gets
  `round(70 − 35 / N)` (half away from zero): 70 only from N = 70 (66 at N = 8, 69 at N = 36); "en iyi kombin 70" holds on days of that size
  (corrected in task L5: the first draft said N = 36, which holds only for a ceiling; the owner kept the rounding).

---

## 10. Week close

**Week**: Monday 00:00 to Sunday 21:00, Europe/Istanbul. **Week points**: the sum of the best `League:BestDays` (5) daily totals of the 7 days
(missing days are 0). It runs in `league-close` right after Sunday's day close, in its own transaction under advisory object 3.

**Final table** per group (pure `WeeklyTable`): members ordered by week points, then the 7-day sum, then competed days, then a hash of
`(week, user)`, so ties are stable and not decided by sign-up order.

**Promotion and relegation** among the members who competed on at least one day (`competed`), as the rules say: the top
`League:PromoteCount` (5) go up a tier, the bottom `League:RelegateCount` (5) go down. Bronze does not go down, Diamond does not go up. The
bands are `min(5, floor(competed / 3))` each (owner's decision), which is 5 from 15 competing members up and keeps a middle band in a small
group where "top 5" and "bottom 5" would otherwise overlap. Members who never
competed "düşmez". **Idle**: at every week close, each joined player with no competed day that week gets `idle_weeks + 1`; at
`League:IdleWeeksToRelegate` (2) they go down one tier (`IdleRelegated`) and the counter restarts, so a long absence costs one tier per two weeks,
down to Bronze.

**Forming next week's groups** (same transaction; visible from Monday 00:00, until then the table shows last week's final):

1. Players: joined, not left, with at least one daily score row in the closing week, at their new tiers. Each gets a new alias (section 4).
2. Per tier, from Bronze upward: if the tier has fewer than `League:MinTierPlayers` (15), merge it with the next tier up (Diamond with the
   one below) and repeat until the set has at least 15 or every tier is in it. Each player keeps their own tier label; promotion moves each
   player from their own tier.
3. Split each set into `ceil(n / League:GroupSize)` groups (30) of near-equal size, ordered by a hash of `(week, user)`: random but
   reproducible, and no one can choose a group.

**Joining mid-week** (a first join, or a player who was not active last week): placed in the group of their tier with the fewest members while
it has fewer than `League:GroupMaxSize` (35), else in a new group, with an alias free in that group. Their week points are the best 5 of the days they have; nothing is prorated,
because "İki gün oynamamak bir şey kaybettirmez" already covers a late start.

**The gap before the week close** (task B9): between Sunday's 21:00 and the moment the week close has succeeded (normally minutes; a failed close is
retried by the next hourly run, into Monday and beyond), nobody is placed into the new week by hand, whoever they are. A placement then would use the
tier the week is about to change, the close would skip the player (it forms groups for players *not yet placed*), and the early groups would fill by
arrival order instead of the close's balanced cut. So `PlaceAsync` places nobody while the week before has an open row the hourly job can still close
(`League:CloseCatchUpDays` after its end; no row at all is the league's first week), and the week close forms the groups. In the gap `GET /league/me`'s
`week` and `GET /league/table` show the week that ended with `state: "Ending"` (`Open` while a week runs, `Final` once closed): last week's table as the
day closes left it, never an error and never a group of one. A player who joined inside the gap has no table yet (`week` null, `/table` 404
`League.NoGroup`, the existing "no group yet") and is placed, with the new tier rule for a late arrival, by the first call after the close.

**Live rank** (screens 02, 04, 05): computed on read from the closed days of the current week, so it moves at each 21:00 close and is never
stored before Sunday.

---

## 11. Style score and creator eligibility

Computed in `league-close` after each week close (pure `StyleScore`), stored in `league.style_scores`. **No endpoint, no screen, no response
field** (the rules, section 6); the founder reads it with SQL.

- **Days counted**: the player's ranked days in the last `League:StyleScore:WindowDays` (90) whose `ranked_count ≥ League:StyleScore:MinRankedPerDay` (30).
- **Score**: `100 × (mean(p) − sd(p) / √n)` over those days, `sd` the sample standard deviation (n − 1); null when n < 2; clamped to 0-100.
  Usage points never enter it.
- **Qualifies this week**: `n ≥ League:StyleScore:MinDays` (30) and `score ≥ League:StyleScore:Threshold` (75).
  `qualifying_weeks` counts consecutive qualifying weekly checks and resets to 0 on a miss.
- **League part met**: at `qualifying_weeks = League:StyleScore:ConsecutiveWeeks` (4) and no entry of the player `Removed` in the last 90 days,
  `league_eligible_at` is set and never cleared by the computation ("Kazanılan hak geri alınmaz").
- **Not checked here**: the public-profile consent and the creator agreement (no data yet), and the creator 18-year declaration (the league consent declares 18+ for the league; whether that is enough for a creator is decided with the creator screen).
  They belong to the creator screen that comes later. Creators stay founder-set (Social rules: `PUT /internal/social/creators/{userId}`); the
  league only marks who has earned the right.
- `revoked_at`/`revoked_reason` exist for "yalnızca kural ihlalinde kaldırılır"; **v1 never revokes** (owner's decision) and writes them
  nowhere. Which violation revokes the right, and through what, is decided with the creator screen.

---

## 12. API contract

All under `/api/v1`, `.RequireAuthorization()`, JSON camelCase, errors as `application/problem+json` with `errorCode`. Enum values are names.
User-facing words are rendered by the app; the backend returns codes, except the copy quoted from the rules and the aliases. With
`League:Enabled = false` (the default) the routes are still mapped (so the OpenAPI document and the authorization matrix see them) but answer
404 `League.Disabled` before doing anything.

| Endpoint | Request | Response | Errors |
|---|---|---|---|
| `GET /league/me` | | `{ joined, consentRequired, consent: { version, text }, tier, joinedAt, today: { day, closesAt, entry: { status, reason, message, pieces[] } \| null, servedPairs, countedVotes, target: 10, limit: 30 }, week: { groupRank, groupSize, points, pointsToPromotion, endsAt } \| null }`; `message` is the Turkish line for the entry's `reason` (`LeagueCopy`: one for each of `RecentlyCompeted`, `UnsafePiece`, `IncompleteOutfit`, `PieceUnavailable`, `AgeRestricted`, `NotWorn`, task B8) and for `Removed`, null otherwise (RecentlyCompeted: "Bu kombin yakın zamanda yarıştı, bugün yarışmıyor."). PR 1 returns the first five fields; `today` and `week` come with entries (PR 2) and groups (PR 4) | 404 `League.Disabled` |
| `POST /league/join` | `{ consentVersion }` | 200 = `GET /league/me` (joining again with the current version is a no-op, 200) | 400 `League.ConsentVersionInvalid` (not the current version), 403 `League.AgeRestricted` |
| `POST /league/leave` | | 204 | 403 `League.NotJoined` |
| `DELETE /league/me/entry` | | 204 | 403 `League.NotJoined`, 404 `League.NoEntryToday`, 409 `League.DayClosed` |
| `GET /league/pairs/next` | | `{ pair: { id, left: { pieces: [{ slot, imageUrl }] }, right: {...}, sequence } \| null, reason: null \| "DailyLimit" \| "NoPairs", countedToday, servedToday, target, limit, closesAt }` | 403 `League.NotJoined`, 409 `League.DayClosed` |
| `POST /league/pairs/{pairId}/vote` | `{ choice, decisionMs }` | `{ counted, chosenWinRate, chosenGames, countedToday, servedToday, target, limit }` | 400 validation, 404 `League.PairNotFound`, 409 `League.AlreadyVoted`, 409 `League.DayClosed` |
| `POST /league/pairs/{pairId}/report` | `{ side, reason? }` | 202 `{ reported: true }` | 404 `League.PairNotFound`, 409 `League.AlreadyReported` |
| `GET /league/results?day=` | `day` = `yyyy-MM-dd`, default the latest closed day | `{ day, entry: { pieces[] } \| null, competed, ranked, rank, rankedCount, comparisons, points: { votes, voting, worn, pieces, total }, piecePointsAvailable, week: { tier, groupRank, pointsToPromotion, endsAt } }` | 404 `League.ResultsNotReady` (day not closed or no row) |
| `GET /league/table` | | `{ weekStart, endsAt, tier, todayPoints, todayIsFinal, promoteCount, relegateCount, tierUp, tierDown, size, rows: [{ rank, points, isMe, alias }] }` | 403 `League.NotJoined`, 404 `League.NoGroup` |
| `GET /internal/league/review-queue` | | `[{ entryId, day, countingReports, reports, pieces[] }]` | internal key |
| `POST /internal/league/entries/{id}/review` | `{ decision: "Restore" \| "Remove" }` | 200 | 404 |
| `POST /internal/league/close` | `{ day? }` | `{ daysClosed, weekClosed }` | 409 `League.CloseInProgress` |

`todayPoints` (screen 05, "Bugünkü puanın") is today's total once today is closed, else the usage points earned so far with `todayIsFinal: false`.
`pointsToPromotion` is the 5th row's points minus the caller's, null when the caller is in the top 5 ("5. sıraya 17 puan").
`piecePointsAvailable` lets screen 04 say "Eklersen 5 puan" only while the weekly cap allows it.

**Screens.**

| Screen | Fed by |
|---|---|
| 01 Lige katıl | `GET /league/me` (`joined: false`, the consent), `POST /league/join` |
| 02 Oylama | `GET /league/pairs/next`, header from `GET /league/me` (tier, group rank, "Sonuçlara" from `closesAt`), "Karar veremedim" = vote `skip` |
| 03 Oy verdikten sonra | the vote response (`chosenWinRate`: "%72 bugünkü galibiyet oranı", or "İlk oylardan biri seninki" when null; progress), then `GET /league/pairs/next` |
| 04 Akşamki sonuç | `GET /league/results` |
| 05 Haftalık lig | `GET /league/table` (aliases, "Sen" for `isMe`) |

**AuthorizationMatrixTests** (`tests/Kombin.Api.IntegrationTests/AuthorizationMatrixTests.cs:36`), one row each:

| Route | Case |
|---|---|
| `GET /api/v1/league/me` | `ScopedRead("/api/v1/league/me")` |
| `POST /api/v1/league/join` | `Exempt("Joins the caller; it takes no ID.")` |
| `POST /api/v1/league/leave` | `Exempt("The caller leaves; it takes no ID.")` |
| `DELETE /api/v1/league/me/entry` | `Exempt("Withdraws the caller's own entry of today; it takes no ID.")` |
| `GET /api/v1/league/pairs/next` | `ScopedRead("/api/v1/league/pairs/next")` (must contain none of A's IDs) |
| `POST /api/v1/league/pairs/{pairId:guid}/vote` | `Probe(B votes on A's pair → 404; A's pair stays unanswered)` |
| `POST /api/v1/league/pairs/{pairId:guid}/report` | `Probe(B reports through A's pair → 404; no report row)` |
| `GET /api/v1/league/results` | `ScopedRead("/api/v1/league/results?day={closedDay}")` |
| `GET /api/v1/league/table` | `ScopedRead("/api/v1/league/table")` |
| the three internal routes | enumerated with the other internal routes (internal key) |

A separate `LeagueAnonymityTests` checks that `pairs/next`, `vote`, `table` and `results` responses of B contain no user ID, display name,
e-mail, entry ID or wardrobe key of A, that the same piece on two days has two different image URLs, and that the same player has different
aliases in two weeks.

---

## 13. Abuse and load

**Multi-account voting.** A sock puppet must be a joined account and sees only random pairs; with 30 pairs a day and N outfits it meets a given
outfit with probability about 60 / N (6 % at N = 1,000), and the first 20 counted votes weigh half. Pairing never lets anyone look for an outfit,
so friends cannot find each other either. What v1 deliberately does not do: device fingerprints, IP correlation of votes (no IP is stored, and the
privacy notes promise none). The first-month metrics (rules, section 8) plus a query of voters whose choices agree unusually often with one
entry's owner group are the watch; a stronger rule is an open question, not a silent addition.

**Reports.** One per player per outfit. The reporter never sees that outfit again. A report **counts** when the reporter's account is at least
`League:Reports:MinAccountAge` (72 h, `users.created_at`) old and has at least `League:Reports:MinCountedVotes` (20) counted votes; Social's
persona rule (`Features/Social/ReportOptions.cs`, `ReportRules.Counts`) is not reused, because the league's own vote count is the better proof
of a real player and reusing it would add a Social reference. Social's "once per author" rule is pointless here: a reporter cannot know the
author. At `League:Reports:HideAt` (3, as `Reports:HideAt` in Social) counting reports the entry leaves the pool at once (`status` stays,
`counting_reports ≥ 3` is in the pairing filter). Every reported entry is in `GET /internal/league/review-queue`; `Restore` clears the count and
puts it back, `Remove` sets `Removed` (out of ranking, no vote points, counts against creator eligibility for 90 days). A removal changes
nothing outside the league: the pieces' `moderation_verdict` and Social's decisions stay as they are (owner's decision). The founder's page for it is `/admin/league` (task B8).

**Known risk: engine outfits can be told apart.** Engine entries show catalogue packshots, human entries wardrobe cut-outs, so a voter may
recognise an engine outfit and vote differently on it. v1 does not try to hide it (no image processing, no mixing). Engine entries are never
ranked, so the harm is limited to the comparisons they take from human outfits and to biased preference data; the first-month metric "engine
win rate against human entries" below shows whether voters treat them differently.

**Rate limits.** A new policy `league` in `Common/RateLimiting` (`RateLimits:League`, 120 requests per 5 minutes per user) on `pairs/next`,
`vote` and `report`; 30 pairs a day is the real cap, the policy only stops a client in a loop. Join, leave, me, results and table stay under the
global fallback. Pinned in `RateLimitTests` like the others.

**Image life.** Copies of a day's thumbnails are deleted by `league-purge` (daily 04:15) `League:Retention:ImageDays` (7) after the day, and
within minutes when an entry is withdrawn, removed, or its player leaves (the next `league-entries` run deletes them). Pairs older than
`League:Retention:PairDays` (30) are deleted (the closed results keep what matters); entries older than `League:Retention:EntryDays` (90, the style
window) are deleted with their pieces.

**First-month metrics** (rules, section 8, plus the engine one): plain SQL in `docs/metrics.sql`, in a section of blocks marked
`-- league:` (not `-- name:`), run by hand with `:day_start` / `:day_end`. `MetricSql.Parse` (`Features/Analytics/MetricSql.cs`) splits the
file at `-- name:` lines only and appends every later line to the current block, so text after the last metric would end up inside it: PR 4
makes a `-- league:` line close the current block without opening a job metric, and a test pins that the `weekly-metrics` job sees the same
four metrics as before. Metabase gets no access to the
schema (section 4). The blocks:

| Block | Measures |
|---|---|
| `league_comparisons` | average and minimum counted comparisons per human entry, and the share of entries not ranked (target < 5 %) |
| `league_join_rate` | joined players over active users, and wardrobe pieces added in the 7 days after joining |
| `league_usage_effect` | rank difference between ranking by vote points only and by total points |
| `league_unmatched_pairs` | share of served pairs whose `match_level` is not `Strict` |
| `league_style_threshold` | players whose style score is at or above 75 |
| `league_engine_win_rate` | weighted win rate of engine entries in counted votes against human entries |

**Load at the close** (estimates of task L1; assumes 60 % of players compete and the average voter answers 15 pairs; the measured close of task L4
is under PR 3 in section 14: 0.7 s at 1,000 players and 2.2 s at 10,000):

| | 1,000 players | 10,000 players |
|---|---|---|
| entries (human + engine) | ~650 | ~6,100 |
| counted pairs read | ~15,000 | ~150,000 |
| Bradley–Terry (≤ 500 iterations × pairs) | ≤ 8 M updates, < 0.2 s | ≤ 75 M updates, < 1.5 s |
| reads | < 0.3 s | ~2 s |
| writes (entries, ~1,000 / 10,000 daily scores) | < 0.5 s | ~4 s (one `INSERT ... SELECT FROM unnest(...)`, not EF row by row) |
| **day close** | **~1 s** | **~10 s** |
| week close (group formation, 10,000 rows) | < 1 s | ~3 s |

Pairing: one indexed query of 200 rows and ≤ 30 pair rows per request, budget p95 ≤ 100 ms. Both budgets get a `LeaguePerformanceTests`
(`KOMBIN_PERF=1`, own container, `[Collection(LatencyBudgets.Name)]`): close ≤ 30 s at 10,000 players, `pairs/next` p95 ≤ 100 ms at 6,000 entries.
Hot-spot: the least-exposed entries' rows are updated by every pair served; the update is one row each, so the contention is short.

---

## 14. Implementation plan

Four PRs, each green on its own, in this order.

**PR 1 — Foundations** (task L2). Move the Istanbul zone and the Hangfire flag to `Common/Time/Istanbul.cs` and
`Common/Hosting/HangfireSettings.cs` and update every caller (the allow-list drops 12 references; no new line). Schema `league` with every table
of section 3, the entities, configurations and one migration; `LeagueOptions` with its validator (`League:Enabled` false by default);
`LeagueConsent` with version `2026-11-v1`. Endpoints: `GET /league/me`, `POST /league/join`, `POST /league/leave`. `AccountPurger` reads the
league image keys; `docs/privacy-notes.md`, `docs/security-review.md`, the README and CLAUDE.md ("League rules") get their sections. No job, no
pairing, no voting, no close.
*Tests:* `LeagueOptionsTests` (defaults = this document, the validator), `LeagueConsentTests` (pinned hash), `LeagueRulesTests` (the age rule),
join/leave/age/disabled integration tests, `AuthorizationMatrixTests` rows, the `AccountDeletionTests` matrix lines, `FeatureBoundaryTests`
(lines deleted).

**PR 2 — Entering and voting.** `TodaysOutfit` (worn only), `RepeatRule`, `EntryMaterializer` (safety through `WardrobeModerator`, image
copies), the `league-entries` job, the `outfit_feedback (created_at)` index; `CatalogOutfitSampler` in Recommendation and `league-fill`;
`pairs/next`, `vote` (with the win rate), `report`, `DELETE /me/entry`; the `league` rate-limit policy; the two `Seam` allow-list lines.
*Tests:* unit: `RepeatRuleTests` (30-day exact, 7-day one-piece, accessories ignored, withdrawn does not block), `PairingTests` (relaxation
order, least exposed first, own entry never, a pair never twice, A always human), `WinRateTests` (including this vote, null below 5 games,
null for a skip); integration: `LeagueEntryTests` (a worn outfit enters, a saved one never does, a wear after 21:00 does not, lock after first
serve, `UnsafePiece`, `Pending` then moderated, `IncompleteOutfit`), `LeagueVotingTests` (30 cap, re-ask returns the same pair, too-fast not
counted, half weight for the first 20, idempotent and conflicting re-votes, day closed), `LeagueAnonymityTests` (pairs and votes),
`RateLimitTests` defaults.

*As built (task L3).* Everything above, plus `LeaguePerformanceTests` for `pairs/next` (p95 23.7 ms at 6,000 entries, 12 voters serving their 30
pairs; budget 100 ms) and the strict age rule (section 4). Where it differs from or adds to this note:

- `UseLeagueJobs` arrives with this PR (it has jobs), not PR 3; it registers `league-entries` and `league-fill` only when `League:Enabled` is
  true and removes them otherwise.
- `GET /league/me` resolves the caller's entry at once but never calls the image safety filter; a piece without a verdict shows `Pending` until
  the next `league-entries` run asks (at most `ModerationRetriesPerRun` pieces a run). No entry is made for a player under an older consent
  version, a disabled player or one whose deletion is pending.
- An outfit dismissed after it was worn is no longer "today's" (it must still be `Worn`).
- `PieceUnavailable` also covers a wardrobe item that no longer exists and a piece with nothing to show (stored thumbnail gone, no catalog
  image); without an image the outfit could not be shown to voters.
- A report uses up an open pair as a skip (never counted): otherwise asking for the next pair would return the same pair, and the reporter would
  see the reported outfit again.
- `chosenGames` is null for a skip, like `chosenWinRate` (a skip chose nothing).
- Vote and report do not require being joined: the pair must be the caller's, and only a player is served pairs (`pairs/next` is 403
  `League.NotJoined`).
- The vote creates the day's `league.days` row if it is missing (`INSERT ... ON CONFLICT DO NOTHING`), so the `FOR SHARE` lock exists before
  the close (PR 3) creates days.
- The `(day) WHERE kind = Engine` index of section 3.1 was not in PR 1's migration and is not added: the fill reads per bucket through the
  pairing index. PR 2's migration (`LeagueVoting`) adds only `outfit_feedback (created_at)`.
- `GET /league/me`'s `week` has its shape (`LeagueWeekResponse`) in the OpenAPI document and is always null until PR 4. The owner's pieces may
  have `imageUrl: null` (a piece without an image any more); a voter's never do.
- In `AuthorizationMatrixTests`, `GET /league/pairs/next` is a probe at noon of a league day of its own (the test clock's header) instead of a
  `ScopedRead`, which runs at the real time and would be refused after 21:00; it still checks that the answer carries none of A's identifiers,
  with A's outfit in the pool. Integration tests pick their time with the `X-Test-Now` header (`TestHeaderClock`).

*Night review fixes (task L3-fix).* A read-only review of master @ 1fda666 found eight defects; each has a test that failed before the fix:

- **Engine entries starved pairing** (critical): candidates are two lists (human, engine) of `League:PairCandidates` each (section 6), and
  `league-fill` fills only buckets with a competing human entry today. `TwelveBucketsOfThreePlayersAndTheirEngineFillHandOutEveryPair`: 36
  players, 324 engine entries, all 1,080 pairs served, every human outfit shown (before: every player got `NoPairs`). Two queries over the old
  index doubled `pairs/next` (p95 47 ms against master's 21 ms on the same machine); the index `(day, status, kind, exposures, id)` brings it
  back to 21-23 ms (`LeaguePerformanceTests`, 6,000 entries).
- **Image copies were byte-identical** to the wardrobe thumbnail (high): `ImageMarker` (section 4); `LeagueAnonymityTests` compares content hashes.
- **Sequence reuse after an account deletion**: `sequence` and the daily limit come from the last sequence; `servedToday` is that number too.
- **Leave raced the entry job**: `WriteAsync` checks the player's standing again under the player's day lock, which `POST /league/leave` now
  takes too; pairing skips entries of players whose `left_at` is set.
- **Changes before the first exposure**: a fresh decision that is not `Competing` replaces a competing entry (copies deleted); an outfit no
  longer worn today leaves the entry `NotCompeting` with the new reason **`NotWorn`** (an OpenAPI enum value). `league-entries` now also
  resolves today's competing entries that have not been shown yet, so this does not wait for the owner to open the app.
- **Report vs vote on one open pair**: the report's skip is `UPDATE ... WHERE answered_at IS NULL`.
- **Vote deadlock** (40P01 → 500): both entries are updated in one statement that locks them in ID order (`FOR NO KEY UPDATE`), and `pairs/next`
  does the same.
- **Repeat rule bypass**: a catalog piece's identity is its product (section 5).
- **The copies are still the same pictures**: consent version `2026-11-v2` says so to the player ("Parçalarını tanıyan biri kombinin sana ait
  olduğunu anlayabilir."), and this note describes anonymity as no name, no profile and no stable identifier in any response, never as
  "cannot be linked". `ImageMarker` stays as it is.

**PR 3 — Day close and scores.** `BradleyTerry`, `DailyPoints`, `LeagueCloseJob.CloseDayAsync`, `GET /league/results`,
`POST /internal/league/close`, `league-purge`, `UseLeagueJobs` after `UseIdentityJobs` in `Program.cs`.
*Tests:* unit: `BradleyTerryTests` (prior pulls an unbeaten outfit below infinity; a known 3-player fit; convergence; same input same output;
two disconnected audiences on one scale), `DailyPointsTests` (the rules' example week: 74 / 0 / 92 / 61 / 0 / 85 / 55 → best five 367; piece cap
on the 4th day; 70 × p rounding, 70 only from N = 70); integration (days in 2020): a full day with 12 players and engine fill, re-run is a no-op,
a crash before the commit leaves nothing, a vote after 21:00 is 409, `LeagueCloseLockTests`; `LeagueAnonymityTests` (results);
`LeaguePerformanceTests`.

*Requirements from the night review (task L3-fix), to build in PR 3:*

- The close computes the ranking threshold (`MinComparisons`) and Bradley–Terry from the **counted pair rows of the day** (`league.pairs`), not
  from the entry counters (`comparisons`, `weighted_*`, which are a display aid for the win rate). Rows with `choice = Skip` are excluded whatever
  `counted` says.
- The close writes a `daily_scores` row with `competed = true` for **every entry that competed**. Until then the repeat rule has nothing to read:
  today the same outfit can compete every day, and `LeagueAnonymityTests` relies on that (the same outfit two days in a row); PR 3 must revisit
  that test (for example a different outfit on the second day, or days more than 30 apart).
- A vote on an entry that was withdrawn, removed or hidden after the pair was served: the close must decide (count it as information about the
  other outfit, as section 4 says for withdrawn entries, or drop it) and document the choice.
- `league.days` rows are created by the first vote (`INSERT ... ON CONFLICT DO NOTHING`, `closed_at` null): the close must treat an existing row
  without `closed_at` as an open day, not as one it already handled.
- `league-purge` must also scan the `league/` storage prefix for copies no `entry_pieces` row points to (a crash between the object write and
  the commit, or a failed delete, leaves them behind).

*As built (task L4).* `BradleyTerry`, `Percentile`, `DailyPoints` (pure, in `Rules/`), `DayCloser` (the close, one transaction), `LeagueCloseJob`
(`league-close`, hourly at :00), `POST /internal/league/days/{date}/close` (`{ day, closedNow, closedAt, rankedCount, engineCount, pairCount,
btIterations, btConverged, closeMs }`; 400 `League.InvalidDay`, 409 `League.DayNotOver` / `League.CloseInProgress`), `GET /league/results?day=`
and `GET /league/results/latest` (404 `League.ResultsNotReady`, 400 `League.InvalidDay`), `LeaguePurgeJob` (`league-purge`, daily 04:15), the
table `league.voter_days` (migration `LeagueDayClose`, which also fills it from existing pair rows), `IObjectStorage.ListAsync` (S3
`ListObjectsV2`, for the orphan sweep), and the result push copy in `Features/Notification/Copy.cs` (`Copy.ForLeagueResult`, not sent: PR 4).
`UseLeagueJobs` registers `league-close` and `league-purge` beside the PR 2 jobs. The decisions:

- **Votes on outfits that changed after they were served** (night review). *Withdrawn*: the votes stay in the fit as information about the
  other outfit (section 4); the withdrawn outfit is an opponent, never ranked, and its owner's row says `competed: false`. *Removed* (a review
  after reports; nothing removes an entry until PR 4's review endpoint): every vote on it is dropped, from the fit and from the opponent's
  comparisons, because a win or a loss against an outfit taken down as unsafe or not clothing says nothing reliable about the other one; it gets
  no strength. *Hidden* (counting reports reached `Reports:HideAt`, not reviewed by the close): **changed in task L5** (the first version left it
  unranked, which let a player wipe a rival's vote points by reporting them). It is ranked like any other outfit from its counted pair rows,
  its rank, percentile and points are stored, and its row carries `under_review`; the stored points count in the week from the start.
  `GET /league/results` shows its owner `status: "UnderReview"`, the message "Kombinin inceleniyor. Sonuç inceleme bitince görünecek.", no rank
  and no vote points (`ranked`, `rank` and `points.votes` null, `points.total` the usage points). `LeagueEntryReviewer` decides it: *Restore*
  removes the mark and the stored result becomes visible; *Remove* makes the entry `Removed`, sets that day's vote points to 0 (total
  recomputed, rank and percentile cleared, `removed_by_review`, `status: "Removed"`), so the live week total follows; **a week already closed is
  not undone** (week points, final ranks, promotions and relegations stay), and **no other outfit's rank or points are recomputed** (N stays).
  A removal on an open day takes the outfit out before the close, which then drops every vote on it. (The owner's default was "removed out,
  withdrawn in"; I agree.)
- **Who is left out**: a player who is disabled or whose deletion is pending at the close: their outfit and **the votes they cast** (a disabled
  account may be the reason for the disabling). A player who left before the close gets no row (section 9); one who left after it keeps the
  day, and leaving after 21:00 no longer withdraws that day's entry (the day is over; before, a leave at 21:00:30 would have taken a competed
  outfit out of its own results).
- **The vote counts come from the pair rows**: the ranking threshold and the fit use the day's pair rows that are `counted`, have a weight and
  are not `Skip` (whatever `counted` says); the entry counters stay a display aid for the win rate. `daily_scores.comparisons` is that count.
- **Every competing entry gets `competed = true`** at the close, ranked or not, so the repeat rule now blocks a repeat
  (`AnOutfitThatCompetedSitsOutTheNextDay`). `LeagueAnonymityTests` closes day one and wears another outfit (the same top, two pieces
  different) on day two.
- **Open days**: a day row without `closed_at` is open whoever created it; a day without a row is closed like any other (every entry unranked,
  the usage points still given, the row created closed).
- **Accounts deleted during the day**: the voting points and the 30-pair cap come from the voter's own counter (`league.voter_days`), so pair rows
  deleted with another account cost the voter nothing (`AnAccountDeletedDuringTheDayCostsTheVotersNothing...`). The same counter replaces "the
  last sequence" of task L3-fix, which still lost a number when the deleted pair was the voter's latest.
- **Day boundary**: everything is half-open at 21:00:00 Istanbul. A vote at 20:59:59.999 counts, one at 21:00:00 is 409; a wear at 21:00:00
  earns nothing that day and, by decision 8, nothing the next day either (the worn window is 00:00-21:00); a piece added at 21:00:00 counts for
  the next day. `GET /league/pairs/next` at 21:00:00 is 409 (the next day's pool opens at midnight). The brief said "belongs to the next day":
  for a vote and a wear that can only mean "not this day", because the league day is 00:00-21:00. **Accepted by the owner (task L5)**: a wear at
  21:00:00 earns nothing on either day.
- **Withdrawing and the close** are serialised like voting: `DELETE /league/me/entry` and `POST /league/leave` hold the day row `FOR SHARE`.
- **Results**: `GET /league/results?day=` (required) and `GET /league/results/latest` (the caller's latest closed day with a row) instead of one
  route with a default; `week` is in the shape and null until PR 4; `piecePointsAvailable` is about the next day's week. Only the caller's own
  row and entry are ever read.
- **Purge**: copies 7 days after their day; pairs and voter counters 30 days and entries 90 days after a **closed** day (an open day keeps its
  inputs until it is closed); objects under `league/` that no entry piece names and that are older than `League:Retention:OrphanGrace` (1 day).
- **Determinism**: entries and pairs are read in ID order, the fit runs in a fixed order, ranks are broken by entry ID; the same rows give the
  same strengths, percentiles, ranks and points to the last bit (`DayCloseRulesTests.TheSameRowsGiveTheSameResultToTheLastBit`,
  `BradleyTerryTests.TheSameGamesInAnyOrderGiveTheSameDoublesToTheLastBit`). Closing twice changes nothing; a close that dies half-way writes
  nothing (one transaction: `ACloseThatDiesHalfWayWritesNothingAndTheNextOneDoesItAll`).

*Where it differs from this note:*

- **The Bradley–Terry iteration.** **Accepted by the owner (task L5)**, who checked independently that both reach the same fixed point;
  `BradleyTerryTests.NewmansFitIsTheMmFixedPoint` pins it (a fixed set of games, MM run to convergence, strengths within 1e-4, same order).
  Section 8's MM step (Hunter 2004) never met the stopping rule within 500 iterations on simulated days of 650
  and 6,100 outfits (15,000 and 150,000 votes), nor on a 30-0 outfit, so every real close would have logged "not converged" with strengths still
  moving. `BradleyTerry.Fit` uses Newman's fixed-point iteration (JMLR 24, 2023) for the same model and prior, which has the same fixed point,
  done in place (Gauss–Seidel): about 160 / 75 sweeps on those days, 25 on a pure chain of wins (where the simultaneous form oscillates for
  ever). The stopping rule (1e-6 on ln π, 500 sweeps) is the note's. The measured closes converged in 122 and 110 sweeps.
- **"70 only from N = 36"** (section 9, first draft) was not what `round(70 × p)` gives: the best of N gets `round(70 − 35 / N)`, which is 69
  for N = 36 and reaches 70 at N = 70 (69.5 rounds up). **Owner's decision (task L5): keep round-half-up**; section 9 now says 70 from N = 70
  (66 at N = 8, 69 at N = 36). `DailyPointsTests` pins N = 8 → 66, 36 → 69, 69 → 69, 70 → 70.
- Two new settings: `League:CloseCatchUpDays` (7) and `League:Retention:OrphanGrace` (1 day).
- The results route is two routes (above); the internal close is `POST /internal/league/days/{date}/close` (the brief), not
  `POST /internal/league/close { day? }`.
- Tests use far-future league days of their own instead of days in 2020 (the existing league tests' convention), and the classes that close
  days share the `LeagueCloses` collection, because the close lock is one per database.
- Not measured at scale: the usage reads (worn outfits, added pieces); the performance test seeds players, entries, pairs and counters only.

*Measured* (`LeaguePerformanceTests`, `KOMBIN_PERF=1`, own container, on a machine with another workload at ~380 % CPU): the close of 1,000
players (650 entries, 15,000 counted pairs) 0.71 s wall (estimate ~1 s), of 10,000 players (6,500 entries, 150,000 pairs) 2.24 s (estimate
~10 s, budget 30 s). `pairs/next` p95 34-44 ms on the same machine, the same as the L3 code measured there (40-41 ms): the counter costs nothing
visible.

**PR 4 — Weeks, aliases, style score, reviews, the result push, the metrics.** `WeeklyTable`, `CloseWeekAsync`, group formation and mid-week
placement, `LeagueAliases` (word lists and the generator), `GET /league/table`, `StyleScore`, the review queue and review endpoints,
Notification's league result push (a `NotificationKind`, a preference), the `-- league:` blocks of `docs/metrics.sql` and the `MetricSql.Parse`
change. The push copy, in `Features/Notification/Copy.cs` (owner's text):

| Case | Title | Body |
|---|---|---|
| ranked | Kombin Ligi sonuçları | Bugünkü kombinin {rank}. oldu. Puanın {total}. |
| not ranked | Kombin Ligi sonuçları | Bugün yeterli oy toplanamadı. Puanın {total}. |

*Tests:* unit: `WeeklyTableTests` (ties, the `min(5, floor(competed / 3))` bands, Bronze and Diamond edges, idle twice), `GroupFormationTests`
(merging below 15, labels kept, sizes), `LeagueAliasTests` (word lists pinned, unique within a group of 35, never last week's alias),
`StyleScoreTests` (the formula, days with fewer than 30 ranked skipped, 4 consecutive weeks, never cleared), `CopyGlossaryTests` (the aliases
and the push copy), `MetricSqlTests` (the job's metrics unchanged); integration: a full week, a mid-week joiner, review restore/remove,
`LeagueAnonymityTests` (table; the same player has different aliases in two weeks), the push respects the preference.

*As built (task L5).* `WeeklyTable`, `GroupFormation`, `LeagueAliases`, `StyleScore` (pure, in `Rules/`), `WeekCloser` (one transaction under
`pg_try_advisory_xact_lock(0x4B4F4D42, 3)`), `LeagueGroups` (the week rows and the mid-week placement), `LeagueTables` (the table read model),
`GET /league/table`, `week` in `GET /league/me` and `GET /league/results`, `GET /internal/league/review-queue` and
`POST /internal/league/entries/{entryId}/review` (over `LeagueEntryReviewer`, which came with the PR 3 fix), and Notification's `league-results`
job with the `LeagueResult` kind and the `leagueResults` preference (migration `LeagueWeeks`: the preference column and a partial index on the
results not pushed yet), and the six `-- league:` blocks of `docs/metrics.sql` with the `MetricSql.Parse` change (a line starting `-- league:`
closes the job's current block; the section opens with one, so its header comment never lands in the job's last metric;
`AnalyticsRulesTests` pins that the job still sees the same four metrics, and `LeagueMetricsSqlTests` runs every league block against the schema).
The league blocks take `:day_start` / `:day_end` (dates) and return named counts, not a (numerator, denominator) pair. The decisions:

- **When weeks close**: `league-close`, after the days of its window, closes every week whose Sunday is in the window (seven days closed, Sunday's
  21:00 passed), oldest first; a failure stops the run and the next hour retries. The internal day-close endpoint closes the day only (not the
  week), so an operator's hand close never runs the week's idleness over the whole database by surprise; the next hourly run closes the week.
- **Who is in a group**: a player is placed (the mid-week rule, except while the week before is not closed: see "The gap before the week close") when they join, when they open `GET /league/me` or `GET /league/table`, and by
  the day close for every player with a result that day who is in no group of that week (someone who only wore an outfit). The week close forms
  the next week's groups from the players with a result row in the closing week, at their new tier; anyone already placed there (a join after
  Sunday's close) keeps their group. Leaving takes the player off the current week's group (a closed week keeps its final table); joining again
  places them again with a new alias; the player row, the consents and every result stay (one history).
- **Ties**: points, the seven-day sum, competed days, then an FNV hash of (week, user), so every rank is distinct and stable; the hash also cuts the
  groups. Group sizes differ by at most one.
- **Idleness** counts every joined player without a competed day, in a group or not; the second idle week in a row costs one tier (Bronze stays) and
  restarts the count.
- **Results under review count in the week** with their stored points (the PR 3 fix); a removal after the close lowers the live week total
  because the table is computed from the daily rows; a closed week is not undone.
- **The table**: an open week is computed on read (the live rank moves at each close and is stored only at the week close); a closed week shows
  what its close stored, renumbered 1..n so a deleted account leaves no gap and no other rank changes. Between Sunday's close and Monday 00:00 the
  table is last week's final one. `todayPoints` is today's total once today is closed (without vote points held back by a review), else the
  usage points earned so far. Rows are `{ rank, points, isMe, alias }` and nothing else.
- **Aliases**: 18 colours × 16 garments (288), drawn with `RandomNumberGenerator`, unique in the group, never the player's alias of the week before.
  They are the one random part of a week close; points, ranks, outcomes, tiers and groups are decided by the rows in a fixed order.
- **Style score**: computed at every week close for every player with a result in the 90 days before the week's end (or a score row): only ranked
  days not under review whose day ranked at least 30 outfits; `qualifying_weeks` and the never-cleared `league_eligible_at` as section 11 says;
  "no entry removed in the last 90 days" means an entry with status `Removed`.
- **The review queue** has an admin page since task B8, `/admin/league` (Restore and Remove through `LeagueEntryReviewer`, which writes the
  `admin.audit_log` row in its own transaction: actor `internal-key` for the internal route, the admin's e-mail for the page). The queue itself is
  `LeagueReviewQueue`, read by both. The page shows what the voters saw (the pieces' image copies), the day, the status, the report count and
  reasons and the under-review mark; **no owner and no reporter**, because the decision is about the picture and the league's promise is no name, no
  profile and no stable identifier. It names four League types from Admin: one `Seam` line in the allow-list.
- **The result push** (`league-results`, every 5 minutes): only for days closed in the last 12 hours (21:00 to 09:00; later, "Bugünkü kombinin"
  would be wrong), once per player and day (`daily_scores.notified_at` set in the same transaction as the notification row, dedupe key
  `league:{day}`), through the alert dispatcher's rules (preference, device, quiet hours); never for a player who left, is disabled or whose
  deletion is pending, nor for a result removed by a review; a result under review waits and is pushed if restored within the window. The
  window is Notification's constant, not a `League:*` setting.

*Where it differs from this note:* the internal close does not close the week (above) and its response has no `weekClosed`; the review decision
is `Restore` or `Remove` as section 12 says (the brief's "clear" is `Restore`); `GET /league/results` gained `status` and `message` (the PR 3 fix);
the review queue items carry `status` and `underReview` besides section 12's fields; `LeagueTableRow` ranks are positions (distinct), never shared.

*Measured* (`LeaguePerformanceTests`, `KOMBIN_PERF=1`, own container, quiet machine): the week close of 1,000 players 1.86 s and of 10,000 players
3.67 s (estimate ~3 s; budget 30 s, the day close's); `GET /league/table` p95 10.3 ms at 10,000 players in groups of 30 (budget 100 ms, the
pairing's). On the same run: `pairs/next` p95 21.9 ms; the day close 0.44 s at 1,000 players and 2.15 s at 10,000 (it now also places the 10,000
players of the seed, who were in no group: 1.32 s before the placement).

### Settings (`League:*` unless noted; every number above)

| Key | Default |
|---|---|
| `Enabled` | **false** (every route answers 404 `League.Disabled`, no job is registered); configuration switches it on when the app screens ship; tests enable it |
| `DayClosesAt` | `21:00` |
| `CloseCatchUpDays` | 7 (task L4: days the hourly close looks back for an unclosed one) |
| `MinAge` | 18 |
| `MaxPairsPerDay` | 30 |
| `VotesForPoints` | 10 |
| `MinDecisionTime` | `00:00:00.600` |
| `NewVoterVotes` | 20 |
| `NewVoterWeight` | 0.5 |
| `MinGamesForWinRate` | 5 |
| `PairCandidates` | 200 |
| `MinComparisons` | 8 |
| `PriorWins` / `PriorLosses` | 0.5 / 0.5 |
| `BtTolerance` | 1e-6 |
| `BtMaxIterations` | 500 |
| `Points:VotesMax` | 70 |
| `Points:Voting` | 15 |
| `Points:Worn` | 10 |
| `Points:Piece` | 5 |
| `Points:PieceDaysPerWeek` | 3 |
| `BestDays` | 5 |
| `GroupSize` / `GroupMaxSize` | 30 / 35 |
| `PromoteCount` / `RelegateCount` | 5 / 5 (each capped at `floor(competed / 3)`) |
| `MinTierPlayers` | 15 |
| `IdleWeeksToRelegate` | 2 |
| `RepeatExactDays` / `RepeatNearDays` / `RepeatNearMaxDifference` | 30 / 7 / 1 |
| `MinPieces` | 2 |
| `ModerationRetriesPerRun` | 50 |
| `EngineFill:MinPool` / `EngineFill:MaxPerRun` | 30 / 20 |
| `Reports:HideAt` / `Reports:MinAccountAge` / `Reports:MinCountedVotes` | 3 / 72 h / 20 |
| `StyleScore:WindowDays` / `MinRankedPerDay` / `MinDays` / `Threshold` / `ConsecutiveWeeks` | 90 / 30 / 30 / 75 / 4 |
| `Retention:ImageDays` / `PairDays` / `EntryDays` / `OrphanGrace` | 7 / 30 / 90 / 1 day (task L4: an unnamed copy younger than this may belong to a transaction still running) |
| `RateLimits:League` | 120 per 300 s |

Not settings: the week (Monday to Sunday, the rules), the job schedules (cron strings next to each job, as everywhere), the advisory lock IDs,
the consent texts and the alias word lists (versioned in code, pinned by tests).

### Open questions

1. **OPEN: counsel has not reviewed the consent text** (versions `2026-11-v1` and `2026-11-v2`, section 4), including its sentence that stands in
   for an age declaration. Not ours to close; `docs/privacy-notes.md` lists it among the questions for counsel. If counsel changes the text, it
   becomes the next version and every player accepts again.

### Decisions taken (task L2)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Names in the group table | none; a per-week generated alias "<colour> <garment>", unique in the group | 3, 4, 10, 12 |
| 2 | Consent text | the owner's text, version `2026-11-v1`, then `2026-11-v2` (task L3-fix: the pieces can be recognised); counsel review open | 4 |
| 3 | "En iyi kombin 70" | the formula stays, rounded half away from zero; 70 only from N = 70 (66 at N = 8, 69 at N = 36; corrected in task L5) | 9 |
| 4 | Small groups | `min(5, floor(competed / 3))` each | 10 |
| 5 | Minors | a known birth year joins only when certainly 18 (`year − birthYear > 18`, strict since task L3); unknown may; the consent is the declaration | 4 |
| 6 | Revoking the creator right | v1 never revokes; the columns stay unwritten | 11 |
| 7 | Near rule | one piece swapped, added or removed, accessories ignored | 5 |
| 8 | After 21:00 | a wear earns nothing that day; a piece counts for the next day | 9 |
| 9 | Who votes | joined players only | 6, 7 |
| 10 | A removed outfit | changes no Social verdict | 13 |
| 11 | Metabase | no access; `-- league:` blocks in `docs/metrics.sql`, run by hand | 4, 13 |
| 12 | Push copy | the owner's two lines | 14 (PR 4) |
| — | Entry | only a worn outfit; saving never enters | 5 |
| — | "% agree" | removed; the chosen outfit's real win rate instead | 7, 12 |
| — | `League:Enabled` | false by default | 1, 12, 14 |
| — | Engine vs human images | accepted risk, measured | 13 |

### Conflicts and findings

- **Image URLs contain the user ID** (`Common/Storage/StorageKeys.cs`: `{area}/{userId}/{ownerId}/thumb.*`). Showing wardrobe thumbnails as
  they are would break anonymity; hence the copies (section 4).
- **The percentile formula** gives "1 en iyi" only if `sıra` counts from the worst; implemented that way (section 8).
- **The Istanbul zone and the Hangfire flag live in Notification**; using them from League would add a forbidden `Legacy` line, so they move
  to `Common` first (section 2).
- **`MetricSql.Parse` appends trailing text to the last metric**, so the league queries need the `-- league:` marker change (section 13).
- No rule was found impossible to implement.
