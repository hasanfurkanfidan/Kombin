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
