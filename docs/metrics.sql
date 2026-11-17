-- Kombin beta metrics, as plain SQL over the analytics.events table and the engagement tables.
--
-- This file is the single source: the weekly-metrics job embeds it (Features/Analytics/MetricSql.cs) and runs each block below with
-- the parameters bound, so what the dashboard shows is what you get by running these queries by hand.
--
-- A COHORT is the set of "actors" whose FIRST app_open falls in [cohort_start, cohort_end). An actor is a signed-in user, or, for a
-- device that never signed in, its anonymous id: COALESCE(user_id::text, anonymous_id). Signing in re-attributes the device's
-- earlier events to the user (IAnalyticsIdentityLinker), so the two never count twice.
--
-- Parameters (bound by the job; for psql pass them with -v, quoted):
--   :cohort_start  inclusive start of the cohort window
--   :cohort_end    exclusive end of the cohort window
--   :as_of         "now": only used by d7_retention to leave out members whose day 8 has not ended yet
--
--   psql "$DATABASE_URL" \
--     -v cohort_start="'2026-10-26 00:00:00+03'" -v cohort_end="'2026-11-02 00:00:00+03'" -v as_of="'2026-11-16 08:00:00+03'" \
--     -f docs/metrics.sql
--
-- Every block, started by a "-- name:" line, returns one row (numerator, denominator). The job stores the percentage
-- (numerator / denominator * 100) in analytics.weekly_metrics. A denominator of 0 means "no data" and writes no row.
-- Days are calendar days in Europe/Istanbul, the product's time zone.

-- name: d7_retention
-- Share of the cohort that opened the app again on day 7 +/- 1 (days 6, 7 and 8) after its first app_open. Only members whose day 8
-- is over at :as_of are counted, so a young cohort has a small denominator instead of a wrong ratio.
WITH opens AS (
    SELECT COALESCE(user_id::text, anonymous_id) AS actor,
           occurred_at,
           (occurred_at AT TIME ZONE 'Europe/Istanbul')::date AS day
    FROM analytics.events
    WHERE name = 'app_open'
      AND COALESCE(user_id::text, anonymous_id) IS NOT NULL
),
first_open AS (
    SELECT actor, MIN(occurred_at) AS first_at, MIN(day) AS first_day
    FROM opens
    GROUP BY actor
),
cohort AS (
    SELECT actor, first_day
    FROM first_open
    WHERE first_at >= :cohort_start AND first_at < :cohort_end
      AND first_day + 8 < (:as_of::timestamptz AT TIME ZONE 'Europe/Istanbul')::date
)
SELECT COUNT(*) FILTER (WHERE retained) AS numerator,
       COUNT(*) AS denominator
FROM (
    SELECT c.actor,
           EXISTS (
               SELECT 1
               FROM opens o
               WHERE o.actor = c.actor
                 AND o.day BETWEEN c.first_day + 6 AND c.first_day + 8
           ) AS retained
    FROM cohort c
) AS members;

-- name: alert_rate
-- Share of the cohort that has set at least one price/stock alert: a row in engagement.price_alerts, or an alert_created event
-- (so an alert that was created and then deleted still counts). Only signed-in users can have alerts.
WITH first_open AS (
    SELECT COALESCE(user_id::text, anonymous_id) AS actor,
           MIN(user_id::text)::uuid AS user_id,
           MIN(occurred_at) AS first_at
    FROM analytics.events
    WHERE name = 'app_open'
      AND COALESCE(user_id::text, anonymous_id) IS NOT NULL
    GROUP BY 1
),
cohort AS (
    SELECT actor, user_id
    FROM first_open
    WHERE first_at >= :cohort_start AND first_at < :cohort_end
)
SELECT COUNT(*) FILTER (WHERE has_alert) AS numerator,
       COUNT(*) AS denominator
FROM (
    SELECT c.actor,
           c.user_id IS NOT NULL AND (
               EXISTS (SELECT 1 FROM engagement.price_alerts a WHERE a.user_id = c.user_id)
               OR EXISTS (SELECT 1 FROM analytics.events e WHERE e.name = 'alert_created' AND e.user_id = c.user_id)
           ) AS has_alert
    FROM cohort c
) AS members;

-- name: click_rate
-- Share of the cohort that has made at least one outbound buy click through /go (engagement.clicks, the revenue record).
-- Anonymous clicks have no user and cannot be attributed to an actor.
WITH first_open AS (
    SELECT COALESCE(user_id::text, anonymous_id) AS actor,
           MIN(user_id::text)::uuid AS user_id,
           MIN(occurred_at) AS first_at
    FROM analytics.events
    WHERE name = 'app_open'
      AND COALESCE(user_id::text, anonymous_id) IS NOT NULL
    GROUP BY 1
),
cohort AS (
    SELECT actor, user_id
    FROM first_open
    WHERE first_at >= :cohort_start AND first_at < :cohort_end
)
SELECT COUNT(*) FILTER (WHERE has_click) AS numerator,
       COUNT(*) AS denominator
FROM (
    SELECT c.actor,
           c.user_id IS NOT NULL
               AND EXISTS (SELECT 1 FROM engagement.clicks k WHERE k.user_id = c.user_id) AS has_click
    FROM cohort c
) AS members;

-- name: onboarding_completion
-- Of the cohort members who started onboarding (at least one onboarding_step event), the share who finished it
-- (an onboarding_step with action = 'done').
WITH first_open AS (
    SELECT COALESCE(user_id::text, anonymous_id) AS actor,
           MIN(occurred_at) AS first_at
    FROM analytics.events
    WHERE name = 'app_open'
      AND COALESCE(user_id::text, anonymous_id) IS NOT NULL
    GROUP BY 1
),
cohort AS (
    SELECT actor
    FROM first_open
    WHERE first_at >= :cohort_start AND first_at < :cohort_end
),
steps AS (
    SELECT COALESCE(user_id::text, anonymous_id) AS actor,
           properties ->> 'action' AS action
    FROM analytics.events
    WHERE name = 'onboarding_step'
)
SELECT COUNT(*) FILTER (WHERE completed) AS numerator,
       COUNT(*) AS denominator
FROM (
    SELECT c.actor,
           BOOL_OR(s.action = 'done') AS completed
    FROM cohort c
    JOIN steps s ON s.actor = c.actor
    GROUP BY c.actor
) AS members;

-- league: the blocks below are the Kombin Ligi first-month metrics, run by hand and never by the weekly-metrics job.
-- ============================================================================================================================================
-- Kombin Ligi, first-month metrics (rules, section 8; docs/design/league.md, section 13). NOT run by the weekly-metrics job: a "-- league:"
-- line closes the job's last block (MetricSql.Parse) and these blocks are run by hand. Each returns one row of named counts, not a
-- (numerator, denominator) pair. Parameters, Istanbul calendar days, end exclusive:
--   psql "$DATABASE_URL" -v day_start="'2026-12-01'" -v day_end="'2027-01-01'" -f docs/metrics.sql
-- The league schema is never granted to metabase_ro: run these as the operator.
-- ============================================================================================================================================

-- league: league_comparisons
-- Counted comparisons per competing outfit (target: average 20, minimum 8) and the outfits that were not ranked (target: under 5 %).
SELECT avg(s.comparisons)::numeric(10, 2)       AS average_comparisons,
       min(s.comparisons)                      AS minimum_comparisons,
       count(*) FILTER (WHERE NOT s.ranked)    AS not_ranked,
       count(*)                                AS competed
FROM league.daily_scores s
WHERE s.competed AND s.day >= :day_start::date AND s.day < :day_end::date;

-- league: league_join_rate
-- Players among the signed-in users who opened the app in the window, and the wardrobe pieces they added in the 7 days after joining.
WITH active AS (
    SELECT DISTINCT e.user_id
    FROM analytics.events e
    WHERE e.name = 'app_open' AND e.user_id IS NOT NULL
      AND e.occurred_at >= :day_start::date::timestamp AT TIME ZONE 'Europe/Istanbul'
      AND e.occurred_at < :day_end::date::timestamp AT TIME ZONE 'Europe/Istanbul'
), joined AS (
    SELECT p.user_id, p.joined_at FROM league.players p JOIN active a ON a.user_id = p.user_id
)
SELECT (SELECT count(*) FROM joined) AS joined_players,
       (SELECT count(*) FROM active) AS active_users,
       (SELECT count(*) FROM joined j JOIN wardrobe.items i ON i.user_id = j.user_id
         AND i.created_at >= j.joined_at AND i.created_at < j.joined_at + interval '7 days') AS pieces_added_within_7_days;

-- league: league_usage_effect
-- How much the usage points move the day's order: the average distance between the rank by vote points only and the rank by total.
WITH ranked AS (
    SELECT rank() OVER (PARTITION BY s.day ORDER BY s.vote_points DESC) AS by_votes,
           rank() OVER (PARTITION BY s.day ORDER BY s.total DESC)       AS by_total
    FROM league.daily_scores s
    WHERE s.ranked AND s.day >= :day_start::date AND s.day < :day_end::date
)
SELECT avg(abs(by_votes - by_total))::numeric(10, 2) AS average_rank_difference,
       count(*)                                      AS ranked_results
FROM ranked;

-- league: league_unmatched_pairs
-- Served pairs that could not keep the same occasion and audience.
SELECT count(*) FILTER (WHERE p.match_level <> 'Strict') AS unmatched,
       count(*)                                          AS served
FROM league.pairs p
WHERE p.day >= :day_start::date AND p.day < :day_end::date;

-- league: league_style_threshold
-- Players whose style score is at or above 75 (at the latest week close), among those with a score.
SELECT count(*) FILTER (WHERE s.score >= 75) AS at_or_above_75,
       count(*) FILTER (WHERE s.score IS NOT NULL) AS scored
FROM league.style_scores s;

-- league: league_engine_win_rate
-- The weighted win rate of engine outfits in counted votes against a player's outfit (do voters treat packshots differently?).
WITH votes AS (
    SELECT p.weight, p.choice, l.kind AS left_kind, r.kind AS right_kind
    FROM league.pairs p
    JOIN league.entries l ON l.id = p.left_entry_id
    JOIN league.entries r ON r.id = p.right_entry_id
    WHERE p.counted AND p.choice <> 'Skip' AND p.day >= :day_start::date AND p.day < :day_end::date
      AND (l.kind = 'Engine') <> (r.kind = 'Engine')
)
SELECT coalesce(sum(weight) FILTER (WHERE (choice = 'Left' AND left_kind = 'Engine') OR (choice = 'Right' AND right_kind = 'Engine')), 0) AS engine_wins,
       coalesce(sum(weight), 0) AS games
FROM votes;

-- tryon: the block below is try-on's list of reported garments, run by hand and never by the weekly-metrics job.
-- ============================================================================================================================================
-- "Manken üstünde gör", reported garments ("Yanlış görünüyor"; docs/design/try-on.md). NOT run by the weekly-metrics job: a "-- tryon:" line
-- closes the job's last block (MetricSql.Parse). There is no review screen yet: this lists the garments users said look wrong, the most reported
-- first (a top shown from the back, most often). A catalogue garment carries its product and brand; a wardrobe garment is counted and shown
-- without anything of its owner. One row per reported garment. Parameters, Istanbul calendar days, end exclusive:
--   psql "$DATABASE_URL" -v day_start="'2026-12-01'" -v day_end="'2027-01-01'" -f docs/metrics.sql
-- The tryon schema is never granted to metabase_ro: run it as the operator.
-- ============================================================================================================================================

-- tryon: tryon_reported_garments
SELECT g.id AS garment_id,
       CASE WHEN g.product_id IS NOT NULL THEN 'catalogue' ELSE 'wardrobe' END AS kind,
       g.product_id, p.name AS product_name, b.name AS brand, g.family, g.prepare_version, g.image_index,
       count(*) AS reports, count(DISTINCT r.user_id) AS reporters, max(r.created_at) AS last_reported_at
FROM tryon.reports r
CROSS JOIN LATERAL unnest(r.garment_ids) AS reported(garment_id)
JOIN tryon.garments g ON g.id = reported.garment_id
LEFT JOIN catalog.products p ON p.id = g.product_id
LEFT JOIN catalog.brands b ON b.id = p.brand_id
WHERE r.created_at >= (:day_start::date)::timestamp AT TIME ZONE 'Europe/Istanbul'
  AND r.created_at < (:day_end::date)::timestamp AT TIME ZONE 'Europe/Istanbul'
GROUP BY g.id, g.product_id, p.name, b.name, g.family, g.prepare_version, g.image_index
ORDER BY reports DESC, last_reported_at DESC, g.id;
