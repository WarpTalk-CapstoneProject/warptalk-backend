-- Billing-owned baseline rate card.
-- CRD is WarpTalk's internal credit unit; time-based rows are priced per second.
-- 0.25 CRD/sec = 15 credits/minute, while voice clone is 40 credits/minute.

INSERT INTO subscription.usage_rate_card
    (charge_type, source_language_code, target_language_code,
     unit_price, currency, effective_from, effective_to)
SELECT rate.charge_type, NULL, NULL, rate.unit_price, 'CRD', TIMESTAMPTZ '2026-07-27 00:00:00+00', NULL
FROM (
    VALUES
        ('STT'::varchar, 0.250000::decimal(12,6)),
        ('TRANSLATION'::varchar, 0.250000::decimal(12,6)),
        ('AUDIO_DUBBING_STANDARD'::varchar, 0.250000::decimal(12,6)),
        ('AUDIO_DUBBING_VOICE_CLONE'::varchar, 0.666667::decimal(12,6))
) AS rate(charge_type, unit_price)
WHERE NOT EXISTS (
    SELECT 1
    FROM subscription.usage_rate_card existing
    WHERE existing.charge_type = rate.charge_type
      AND existing.currency = 'CRD'
      AND existing.source_language_code IS NULL
      AND existing.target_language_code IS NULL
      AND existing.effective_to IS NULL
);
