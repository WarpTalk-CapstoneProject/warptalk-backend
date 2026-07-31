-- Migration: 039-26-07-2026-seed-phase2-billing-rate-card
-- Description: Seed Phase 2 billing rate card for current production models.
--
-- UNITS — read this before touching any number below.
--   provider_unit_cost is USD per unit, as published by the provider.
--   unit_price is CREDITS per unit, NOT currency, even though `currency` reads 'VND'.
--     `currency` records which currency the credit conversion was priced in; it does not
--     mean unit_price is denominated in it.
--
--   Each unit_price was derived as:
--       provider_unit_cost * markup_multiplier * FX_USD_VND / CREDIT_VALUE_VND
--     with FX_USD_VND = 26300 and CREDIT_VALUE_VND = 4
--     e.g. STT/second: 0.0001 * 2.5 * 26300 / 4 = 1.643750
--
--   Both constants also live in subscription.billing_pricing_config (migration 042) as
--   admin-editable rows ('fx_rate_usd_vnd', 'credit_value_vnd'). They are baked into the
--   literals here on purpose: settlement snapshots an immutable rate, so repricing must not
--   retroactively change what past usage cost. The consequence is that editing either config
--   row does NOT reprice these seeded rows — a repricing needs a new migration that closes
--   the current rows (effective_to = now()) and inserts recomputed ones.


WITH seed_rows (
    charge_type,
    unit,
    currency,
    provider,
    model,
    provider_unit_cost,
    markup_multiplier,
    unit_price,
    source_language_code,
    target_language_code,
    notes
) AS (
    VALUES
        ('STT', 'second', 'VND', 'openai', 'gpt-4o-transcribe',
         0.0001000000::numeric, 2.5000::numeric, 1.643750::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4o-transcribe, estimated $0.006/minute = $0.0001/second.'),

        ('TRANSLATION', 'token_in', 'VND', 'openai', 'gpt-4.1-mini',
         0.0000004000::numeric, 2.5000::numeric, 0.006575::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1-mini uncached input tokens.'),
        ('TRANSLATION', 'token_in_cached', 'VND', 'openai', 'gpt-4.1-mini',
         0.0000001000::numeric, 2.5000::numeric, 0.001644::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1-mini cached input tokens.'),
        ('TRANSLATION', 'token_out', 'VND', 'openai', 'gpt-4.1-mini',
         0.0000016000::numeric, 2.5000::numeric, 0.026300::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1-mini output tokens.'),

        ('AUDIO_DUBBING_STANDARD', 'character', 'VND', 'cartesia', 'sonic-3.5',
         0.0000392000::numeric, 3.0000::numeric, 0.773220::numeric, NULL::varchar, NULL::varchar,
         'Cartesia Sonic 3.5 standard TTS, priced per generated character.'),
        ('AUDIO_DUBBING_VOICE_CLONE', 'character', 'VND', 'cartesia', 'sonic-3.5-clone',
         0.0000588000::numeric, 3.5000::numeric, 1.353135::numeric, NULL::varchar, NULL::varchar,
         'Cartesia voice clone TTS runtime, priced per generated character.'),

        ('VOICE_CLONE_ENROLLMENT', 'profile', 'VND', 'cartesia', 'cartesia-localizing-voice',
         0.0088200000::numeric, 3.5000::numeric, 202.970250::numeric, NULL::varchar, NULL::varchar,
         'Cartesia Localizing a voice = 225 Cartesia credits/profile; maps to one enrollment event.'),

        ('AI_ASSISTANT', 'token_in', 'VND', 'openai', 'gpt-4.1',
         0.0000020000::numeric, 2.5000::numeric, 0.032875::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1 uncached input tokens.'),
        ('AI_ASSISTANT', 'token_in_cached', 'VND', 'openai', 'gpt-4.1',
         0.0000005000::numeric, 2.5000::numeric, 0.008219::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1 cached input tokens.'),
        ('AI_ASSISTANT', 'token_out', 'VND', 'openai', 'gpt-4.1',
         0.0000080000::numeric, 2.5000::numeric, 0.131500::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4.1 output tokens.'),

        ('AI_SUMMARY', 'token_in', 'VND', 'openai', 'gpt-4o-mini',
         0.0000001500::numeric, 2.5000::numeric, 0.002466::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4o-mini uncached input tokens.'),
        ('AI_SUMMARY', 'token_in_cached', 'VND', 'openai', 'gpt-4o-mini',
         0.0000000750::numeric, 2.5000::numeric, 0.001233::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4o-mini cached input tokens.'),
        ('AI_SUMMARY', 'token_out', 'VND', 'openai', 'gpt-4o-mini',
         0.0000006000::numeric, 2.5000::numeric, 0.009863::numeric, NULL::varchar, NULL::varchar,
         'OpenAI gpt-4o-mini output tokens.')
)
INSERT INTO subscription.usage_rate_card (
    id,
    charge_type,
    unit,
    currency,
    provider,
    model,
    provider_unit_cost,
    markup_multiplier,
    unit_price,
    source_language_code,
    target_language_code,
    effective_from,
    is_active,
    notes
)
SELECT
    uuidv7(),
    s.charge_type,
    s.unit,
    s.currency,
    s.provider,
    s.model,
    s.provider_unit_cost,
    s.markup_multiplier,
    s.unit_price,
    s.source_language_code,
    s.target_language_code,
    now(),
    true,
    s.notes
FROM seed_rows s
WHERE NOT EXISTS (
    SELECT 1
    FROM subscription.usage_rate_card existing
    WHERE existing.charge_type = s.charge_type
      AND existing.unit = s.unit
      AND existing.currency = s.currency
      AND existing.provider = s.provider
      AND existing.model = s.model
      AND COALESCE(existing.source_language_code, '') = COALESCE(s.source_language_code, '')
      AND COALESCE(existing.target_language_code, '') = COALESCE(s.target_language_code, '')
      AND existing.is_active = true
      AND existing.effective_to IS NULL
);

COMMENT ON COLUMN subscription.usage_rate_card.unit_price IS
    'CREDITS per unit, not currency. Derived as provider_unit_cost * markup_multiplier * fx_rate_usd_vnd / credit_value_vnd. Editing billing_pricing_config does not reprice existing rows — close them with effective_to and insert recomputed ones.';

COMMENT ON COLUMN subscription.usage_rate_card.provider_unit_cost IS
    'USD per unit, as published by the provider.';

COMMENT ON COLUMN subscription.usage_rate_card.currency IS
    'Currency the credit conversion was priced in. unit_price is still expressed in credits, not in this currency.';

