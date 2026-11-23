# Handover: "Arkadaşına sor" (ask a friend), 2026-11-23 evening

Branch `task-af-ask-friend` in both `Kombin` (backend) and `kombin-frontend` (mobile). No PRs opened yet, nothing merged.
Spec: `docs/design/ask-a-friend.md` (section 9 has the PR split and the test names).

## Done

| Piece | Repo | Commits | State |
|---|---|---|---|
| AF1 design note | backend | 6b3b739 | done |
| AF2 data, consent, asker API (`/api/v1/me/asks`), image copies, purger step, privacy notes, CLAUDE.md rules | backend | 21d1e58, 23a29a1, c4dd135, abc5827 | full suite 2678 tests, 0 failed; build and `dotnet format` clean |
| M1 ask and share (button, second-outfit picker, consent sheet, share sheet, error lines) | frontend | fef0398, dd71f76, d369be5 | `npm run verify` green, 1475 tests |

Deviations from the note are recorded in the note's section 5 (piece cap per outfit, catalog fallback, `answeredAt` = `updated_at`,
`MaxPushesPerAsk` moved to AF3, extra DB check constraints).

## Remaining

1. **AF3 (backend)**: the public Razor page `/sor/{token}` (GET/POST, `/bildir`, `/indir`), `AskNoteRules`, the `askfriend-public` rate-limit
   policy, reports and hiding, the `ask-answers` push job (kind `AskAnswer`, preference `AskAnswers`, `Notifications:AskAnswers:MaxPushesPerAsk`),
   `askfriend-purge` job + `UseAskFriendJobs`, metrics.sql block, README and security-review entries. Notes from AF2: set `notified_at` with a bulk
   `ExecuteUpdate` (so `answeredAt` does not move); when close could not delete copies, `copies_deleted_at` stays null and the purge job finishes it.
2. **M1 contract swap (frontend)**: the backend has no committed `docs/openapi.json`; the contract is served at `/openapi/v1.json` by a
   Development API built from this branch. Regenerate the app's contract from it and replace the hand-written `src/features/askFriend/types.ts`
   (marked TODO) with generated types.
3. **M2 (frontend)**: "Sorduklarım" list, result view (tally, answers, hide, close, share again), deep link `kombin://asks/{id}`, notification
   toggle `askAnswers`; after AF3's contract.
4. **Review and PRs**: independent review subagent on each branch, then PRs; merge go-ahead from the coordinator.
5. **Open owner questions**: section 10 of the design note (defaults are already in the code).
