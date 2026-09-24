-- Provider cost on the internal credit-unit (CRD) rate cards.
--
-- WHY
--   billing_worker settles every charge on a CRD card (BillingRepository.record_usage_and_charge,
--   currency default 'CRD'). Those cards came from 001 and 016 with no unit and no
--   provider_unit_cost, so the admin Insights "AI provider cost" (Σ usage quantity ×
--   provider_unit_cost of the card each consume row references) covered ~0% of production usage.
--   The VND cards from 006 do carry provider costs, but priced per token / per character, while
--   every CRD charge is metered in SECONDS. This migration puts a per-second cost on the CRD cards
--   where the repository already holds a real provider price, and on nothing else.
--
-- UNITS
--   provider_unit_cost is USD per unit (column comment from 006). billing_worker reports:
--     TRANSLATION               unit 'second', quantity = seconds of source speech in the segment
--     AUDIO_DUBBING_STANDARD    unit 'second', quantity = duration of the synthesised clip
--     AUDIO_DUBBING_VOICE_CLONE unit 'second', quantity = duration of the synthesised clip
--     STT                       unit 'second' (charged 2026-07-28 → 2026-08-10 only; WT-344 made it free)
--     AI_ASSISTANT              unit 'token', combined decide+generate tokens (2026-08-02 → 2026-08-10 only)
--   001 already documents its time-based rows as priced per second, and 016 its AI_ASSISTANT row
--   per token; step 1 writes that down in the `unit` column so the Insights reader's unit guard
--   (card unit must equal the usage record's unit) means something for CRD cards.
--
-- PRICES (every one exact; BillingProviderCostMigrationTests re-derives each literal)
--   STT                       0.00005  USD/s = $0.003/min ÷ 60.
--       OpenAI gpt-4o-mini-transcribe, the production STT_MODEL for the whole window STT was
--       charged (warptalk-infrastructure deploy/production/app.compose.yml, 2026-07-28 → 2026-08-10).
--       Price: $0.003/min, recorded in the team decision log (workspace DECISIONS.md, "STT —
--       gpt-4o-mini-transcribe"), which is OpenAI's published price for that model.
--   AUDIO_DUBBING_STANDARD    0.00049  USD/s = $0.0000392/character × 12.5 characters/s.
--       Cartesia sonic-3.5 (production TTS_MODEL since the platform's first deploy). The per-character
--       price is 006's AUDIO_DUBBING_STANDARD provider_unit_cost (Startup plan, $49 / 1,250,000
--       credits, 1 credit per character). 12.5 characters/s is Cartesia's own published equivalence
--       of 1,250,000 credits ≈ 1,667 minutes (workspace docs/credit-economics.md §2 CHARS_PER_SEC, which
--       tabulates this exact per-second figure in §3.3; DECISIONS.md independently records ~$0.03/min).
--   AUDIO_DUBBING_VOICE_CLONE 0.000735 USD/s = $0.0000588/character × 12.5 characters/s.
--       006's AUDIO_DUBBING_VOICE_CLONE provider_unit_cost (1.5 credits per character), same rate.
--
--   NOT COVERED, on purpose — no price for what these cards meter exists in the repository:
--     TRANSLATION  production runs gpt-realtime-2.1 (first sentence) and gpt-4.1 (the rest, the
--                  fallback and backfill). 006 prices gpt-4.1-mini per token, and nothing converts
--                  tokens to seconds of source speech for either current model. The owner has to
--                  supply USD per second of source speech; /admin/plans can now record it.
--     AI_ASSISTANT per-token cost of a combined input+output count across two model calls; 006
--                  prices input and output separately, so no single honest per-token figure exists.
--
-- HISTORY
--   No credit_transactions row is touched. Insights derive cost at read time from the card each
--   row references, so filling a MISSING cost on a card applies to everything already settled on
--   it — which is correct here, because the provider and model behind each covered card has not
--   changed during any CRD card's lifetime (see the windows above). That is also why closed or
--   deactivated CRD cards of the same charge type get the same cost: they priced the same model.
--   A later provider price change must NOT edit a card in place; /admin/plans supersedes the card
--   (new row, same credit price) so settled usage keeps the cost that applied to it.
--
-- SAFETY
--   * unit_price, currency, provider, model and the effective window are not modified, so the
--     worker's rate lookup (charge_type + currency + language + effective window) and every
--     snapshot price are unchanged.
--   * provider and model stay NULL, so ux_usage_rate_card_active_lookup (which includes them)
--     cannot be violated even if production holds duplicate open CRD rows.
--   * Idempotent: only rows still missing the value are updated; a cost an admin has entered is
--     never overwritten.

-- 1. The unit each CRD card already meters in.
UPDATE subscription.usage_rate_card
SET unit = 'second'
WHERE currency = 'CRD'
  AND unit IS NULL
  AND charge_type IN ('STT', 'TRANSLATION', 'AUDIO_DUBBING_STANDARD', 'AUDIO_DUBBING_VOICE_CLONE');

UPDATE subscription.usage_rate_card
SET unit = 'token'
WHERE currency = 'CRD'
  AND unit IS NULL
  AND charge_type = 'AI_ASSISTANT';

-- 2. USD per second, where a real price exists.
WITH provider_cost (charge_type, provider_unit_cost, note) AS (
    VALUES
        ('STT'::varchar,
         0.0000500000::numeric,
         'Provider cost: OpenAI gpt-4o-mini-transcribe $0.003/min = $0.00005/s (DECISIONS.md).'),
        ('AUDIO_DUBBING_STANDARD'::varchar,
         0.0004900000::numeric,
         'Provider cost: Cartesia sonic-3.5 $0.0000392/char x 12.5 char/s = $0.00049/s (006; docs/credit-economics.md).'),
        ('AUDIO_DUBBING_VOICE_CLONE'::varchar,
         0.0007350000::numeric,
         'Provider cost: Cartesia sonic-3.5 clone $0.0000588/char x 12.5 char/s = $0.000735/s (006; docs/credit-economics.md).')
)
UPDATE subscription.usage_rate_card card
SET provider_unit_cost = cost.provider_unit_cost,
    notes = CASE
        WHEN card.notes IS NULL OR card.notes = '' THEN cost.note
        ELSE card.notes || ' | ' || cost.note
    END
FROM provider_cost cost
WHERE card.currency = 'CRD'
  AND card.charge_type = cost.charge_type
  AND card.unit = 'second'
  AND card.provider_unit_cost IS NULL;
