-- Google Meet plugin: also require https://www.googleapis.com/auth/meetings.space.readonly.
--
-- WHY
--   WarpTalk is going to read Meet REST v2 conferenceRecords (participants, participantSessions,
--   transcripts.entries) for meetings the user attends. meetings.space.created only reaches spaces
--   this app created, which excludes every meeting someone else scheduled, so the Meet plugin asks
--   for meetings.space.readonly. Nothing calls Meet REST yet; this change only widens the grant.
--
-- WHAT CHANGES
--   Only required_scopes_json on the google_meet row. The plugin keeps calendar.events (its
--   existing create-meeting tool needs it) and gains meetings.space.readonly. Tool bodies and their
--   per-tool requiredScopes are untouched, so the existing tool keeps working on an old grant.
--
-- EFFECT ON USERS ALREADY CONNECTED
--   Their stored Google grant lacks the new scope, so the catalog row's requiredScopes is no longer
--   a subset of its grantedScopes and the plugins page / WarpBot read Meet as "connect it" instead of
--   Connected. Connect goes back to Google with Meet's scopes and include_granted_scopes=true, so the
--   consent is incremental and Calendar/Drive keep what they had.
--
-- IDEMPOTENT
--   Each scope is appended only when absent, existing order is kept, and the WHERE matches only a
--   row that is actually missing one, so a re-run writes nothing and does not churn updated_at.
--   The retired google_workspace row is left alone: it is inactive and nobody consents through it.

WITH wanted (scope, ord) AS (
    VALUES
        ('https://www.googleapis.com/auth/calendar.events', 1),
        ('https://www.googleapis.com/auth/meetings.space.readonly', 2)
),
target AS (
    SELECT p.id, COALESCE(p.required_scopes_json, '[]'::jsonb) AS scopes
    FROM assistant.plugins AS p
    WHERE p.plugin_key = 'google_meet'
      AND jsonb_typeof(COALESCE(p.required_scopes_json, '[]'::jsonb)) = 'array'
      AND EXISTS (
          SELECT 1
          FROM wanted AS w
          WHERE NOT (COALESCE(p.required_scopes_json, '[]'::jsonb) ? w.scope)
      )
),
patched AS (
    SELECT
        t.id,
        t.scopes || COALESCE(
            (
                SELECT jsonb_agg(to_jsonb(w.scope) ORDER BY w.ord)
                FROM wanted AS w
                WHERE NOT (t.scopes ? w.scope)
            ),
            '[]'::jsonb
        ) AS scopes
    FROM target AS t
)
UPDATE assistant.plugins AS p
SET
    required_scopes_json = patched.scopes,
    updated_at = now()
FROM patched
WHERE p.id = patched.id;
