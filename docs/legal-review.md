# Legal texts: what a lawyer has to look at

The public pages (`/legal/privacy`, `/legal/terms`, `/legal/delete-account`; `src/Kombin.Api/Features/Legal/Pages`) are **drafts** written by an engineer
from `docs/privacy-notes.md`. They are in Turkish, in plain language, and they say what the code does today. Every sentence or paragraph that needs a
lawyer's eye is preceded by an HTML comment `<!-- LAWYER -->` in the source (25 of them; they are also in the served HTML, so remove them when the texts are
approved). Find them with `grep -rn "LAWYER" src/Kombin.Api/Features/Legal/Pages`. Nothing below is a legal opinion.

When a text changes: edit the page, update `docs/privacy-notes.md` if a fact changed, and raise `Legal:Version` (`LEGAL_VERSION`) so the app can tell users.

Placeholders `{{CONTROLLER_NAME}}`, `{{CONTACT_EMAIL}}`, `{{ADDRESS}}` come from `Legal:*` (production refuses to start while they are blank): who the
controller is (a person, a company in formation?) is the first thing to settle.

## Privacy policy and KVKK notice (`Privacy.cshtml`)

| # | Where | What to check |
|---|---|---|
| P1 | §1 Veri sorumlusu | One contact address for privacy requests: enough for KVKK m. 10 and the Kurul's rules on the application procedure? Is the controller registered, or exempt, at VERBİS? |
| P2 | §2, after the table | Wardrobe cutouts and thumbnails are served from a public bucket by an unguessable URL (anyone with the URL can fetch them); the original is private. Is that disclosure, and the design, acceptable for photos that may show a person? |
| P3 | §3 Hukuki sebepler | The mapping of purposes to KVKK m. 5/6 legal bases (contract, legitimate interest, legal obligation) and explicit consent for posts. Whether photos of people in posts are sensitive data (m. 6) needing explicit consent beyond the posting consent text. |
| P4 | §4, after the table | Transfers abroad (Expo and, if enabled, the AI narration service: US) under KVKK m. 9 as amended in 2024: which mechanism (adequacy, standard contract with notification to the Kurul, explicit consent), and what the text must say. The EU hosting claim must match the provider chosen. |
| P5 | §4, "Verilerini satmayız" | An absolute promise (no sale, no sharing with advertisers). True today; confirm the wording is one the company wants to be held to. |
| P6 | §5, backups | "Backups are kept at most 7 days and deleted data leaves them then" (the backup script keeps 7 days). Acceptable as the deletion statement? |
| P7 | §5, blocked posts and admin log | Blocked posts kept 30 days; the admin audit log kept without an end date (holds no personal data, only IDs and the reasons authors were shown). A retention period is needed (privacy notes, open question 10). |
| P8 | §5, statutory retention | The catch-all for retention duties of the law (tax, e-commerce): is there any for a free app that sells nothing, and which ones apply to affiliate income records? |
| P9 | §6 Haklar | The list of KVKK m. 11 rights, as worded. |
| P10 | §6 Başvuru | Answer "within 30 days at most", identity verification by writing from the account's address, and the pointer to a complaint to the Kurul. |
| P11 | §8 Çocuklar | 13+ and "not directed at children": there is no age gate in the app. Turkish law (age of majority 18, consent of a parent below), KVKK's position on minors, and the stores' rules (Apple, Google Play "Families") decide the real number and whether a gate is required. |
| P12 | §7 Güvenlik | The breach notification promise (KVKK m. 12: to the Kurul and to the people within 72 hours) and the security claims. |
| P13 | §9 Komisyon | The affiliate disclosure "Kombin satış yapmaz; bağlantılardan komisyon kazanabilir" and the creator wording "Bu içerikten komisyon kazanabilir": is it enough under the advertising rules (Ticari Reklam ve Haksız Ticari Uygulamalar Yönetmeliği, the Reklam Kurulu's guidance on promotions and influencers)? Where must it appear in the app? |

## Terms of use (`Terms.cshtml`)

| # | Where | What to check |
|---|---|---|
| T1 | §1, prices | Prices and stock are shown for information and may be out of date; the user must check the seller's page. Enough against the consumer-protection and price-information rules for a comparison service? |
| T2 | §1, "Komisyon bilgisi" | The commission disclosure again (see P13) and that it does not raise the user's price. |
| T3 | §2 Hesap | The minimum age (13, see P11) and the user's duties for the account. |
| T4 | §3, license | The licence the user gives for their posts: limited, non-exclusive, free, for showing, resizing and reprocessing, ending at deletion. Scope, duration and whether it must be explicit about creators' posts earning commission. |
| T5 | §3, removal | Hiding and removing content without notice, and the "three reports hide a post" rule. Notice-and-action duties for a platform (Law 5651) and the rights of the author. |
| T6 | §5 Fikri haklar | The ownership statement and the brand/seller disclaimer. |
| T7 | §6 Sorumluluk | The limitation of liability ("as is", no liability for sellers, user content, indirect damage) against the consumer law's limits (unfair terms). |
| T8 | §8 Değişiklikler | Changes take effect on continued use: whether that is valid consent to changed terms for consumers. |
| T9 | §9 Hukuk | Turkish law and the competent courts (the controller's seat; consumer courts and arbitration committees keep their jurisdiction). |

## Account deletion page (`DeleteAccount.cshtml`)

| # | Where | What to check |
|---|---|---|
| D1 | "Ne olur?", backups | The same statement as P6. |
| D2 | Table, last row | The admin audit log is kept and holds no personal data except the deleted user's ID as a target (see P7). |
| D3 | "Uygulamaya giremiyorsan" | The 30-day answer period for requests sent by e-mail (see P10), and that identity is checked by writing from the account's address. |

The page also carries an HTML comment `<!-- CHECK-WITH-APP -->` where it names the app's menu path (Profil → Ayarlar → Hesabımı sil): confirm it with the app
team; it is not a legal question. Google Play requires this page's URL in the data-safety form; the stores also want the privacy policy URL.

## Not marked, but worth a question

- The posting consent text (`Features/Social/PostingConsent.cs`, version `2026-11-v1`) is versioned and pinned by a test; the terms refer to it. Lawyers may change
  it only by adding a new version.
- Visual search processes photos of strangers in memory only. The legal pages say the photo is not stored; whether an in-app notice on the lens screen is needed
  is privacy notes, open question 9.
- Creator terms (a separate agreement for creators, payouts) do not exist yet (privacy notes, open question 7).
