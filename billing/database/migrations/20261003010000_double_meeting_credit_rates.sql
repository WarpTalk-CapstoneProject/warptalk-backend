-- Meeting credit rates ×2 (owner decision, 2026-10-02).
--
-- What a meeting pays per second of source speech (translation) or of rendered audio (dubbing) is the
-- CRD card billing_worker resolves for the charge type. Those three cards were seeded on 2026-07-27
-- and nothing in the admin portal could change them: the rate-card editor only accepts the
-- provider-cost (USD) identities. From this release they are editable under Admin → Platform
-- settings (PUT /api/v1/usages/rate-card/{id}/credit-price), and this migration moves the default.
--
--     TRANSLATION                 0.25      → 0.5       credits / second
--     AUDIO_DUBBING_STANDARD      0.25      → 0.5
--     AUDIO_DUBBING_VOICE_CLONE   0.666667  → 1.333334
--
-- A SUPERSEDE, NOT AN UPDATE. credit_transactions.pricing_rate_card_id points at the card a charge
-- was settled on, and that card has to keep saying what was actually charged. So each open card is
-- closed and a copy opened at twice the price, exactly as the admin endpoint does it.
--
-- Whatever is open is doubled, rather than the three literals above being written: a database
-- whose rates were already moved keeps its ratio to this default. STT and AI_ASSISTANT also have
-- CRD cards but are free (WT-344, billing_worker never reads them), so they are left alone.
--
-- No BEGIN/COMMIT: the migration runner owns the transaction.

CREATE TEMP TABLE _doubled_cards ON COMMIT DROP AS
SELECT *
FROM subscription.usage_rate_card
WHERE currency = 'CRD'
  AND unit = 'second'
  AND charge_type IN ('TRANSLATION', 'AUDIO_DUBBING_STANDARD', 'AUDIO_DUBBING_VOICE_CLONE')
  AND is_active
  AND effective_to IS NULL;

UPDATE subscription.usage_rate_card c
SET is_active = false,
    effective_to = now()
FROM _doubled_cards d
WHERE c.id = d.id;

INSERT INTO subscription.usage_rate_card (
    charge_type, source_language_code, target_language_code, unit_price, currency,
    effective_from, unit, provider, model, provider_unit_cost, markup_multiplier, is_active, notes)
SELECT
    charge_type, source_language_code, target_language_code, round(unit_price * 2, 6), currency,
    now(), unit, provider, model, provider_unit_cost, markup_multiplier, true,
    'Meeting credit rates x2 (2026-10-02): was ' || unit_price::text
FROM _doubled_cards;
