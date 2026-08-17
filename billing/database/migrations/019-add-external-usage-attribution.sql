-- 019-add-external-usage-attribution.sql
-- Ticket: WT-446
--
-- Splits a workspace's own spend from its guests', as its own column on usage_records.
--
-- WHY THIS IS A GENERATED COLUMN AND NOT AN 18th SETTLEMENT ARGUMENT
--   The obvious shape is a p_is_external parameter on subscription.settle_usage_charge. That
--   function decides whether a workspace can pay at all — balance, overage, suspension — and
--   Postgres cannot add a parameter in place: it has to be DROPped and recreated in full, which
--   is what 017 did to its ~200 lines. Rewriting all of it to carry one reporting flag puts the
--   entire settlement path at risk for something that does not participate in a single pricing
--   decision. The flag is a dimension, not an input.
--
--   `details` is already an argument, already jsonb, already persisted on the row, and already
--   written solely by the AI settlement worker. Generating the column from it gives a real,
--   indexable, queryable field — which is what was asked for — with ZERO changes to the function
--   that moves money, and it cannot drift from its source because it IS its source.
--
-- WHY THE EXPRESSION NEVER CASTS
--   `(details->>'is_external')::boolean` would be the natural read, and it is a trap: a value
--   that is not boolean-shaped raises 22P02 on INSERT, and that INSERT is the settlement. One bad
--   details payload would stop billing outright. Comparing as text is total — anything unexpected
--   is simply not external — so no value of `details` can ever fail a charge.
--
--   The expression must also be IMMUTABLE for a STORED generated column. `->>` and `IN` are;
--   a cast to boolean is not guaranteed to stay that way across types.
--
-- BACKFILL
--   STORED, so existing rows are computed once by this ALTER. Every historical row has no
--   is_external key and becomes false, which is correct: they were recorded before the product
--   could tell, and false is "not known to be external" rather than an invented fact.

ALTER TABLE subscription.usage_records
    ADD COLUMN IF NOT EXISTS is_external boolean
    GENERATED ALWAYS AS (
        COALESCE(details ->> 'is_external', '') IN ('true', 't', '1')
    ) STORED;

COMMENT ON COLUMN subscription.usage_records.is_external IS
    'WT-446: the speaker was not a member of the room''s workspace when admitted. Generated from details->>''is_external'', which the AI settlement worker sets from translationRoom:<room>:external_participants. Reporting dimension only — it takes no part in pricing.';

-- Reporting reads this as "this workspace''s external spend this cycle", which is a filter on top
-- of the subscription+time predicate the credit views already use. Partial, because the whole
-- point of the column is that true is the rare side.
CREATE INDEX IF NOT EXISTS usage_records_external_subscription_idx
    ON subscription.usage_records (subscription_id, recorded_at)
    WHERE is_external;
