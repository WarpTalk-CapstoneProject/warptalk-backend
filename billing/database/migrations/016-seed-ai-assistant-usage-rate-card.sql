-- AI_ASSISTANT baseline rate card, in the same internal credit unit (CRD) as 001.
--
-- Why this is needed: migration 039 already seeds AI_ASSISTANT rows, but those are priced
-- in VND, while billing_worker settles every charge with the default currency 'CRD' (see
-- BillingRepository.record_usage_and_charge). Its rate-card lookup filters on
-- charge_type + currency, so without a CRD row an AI_ASSISTANT charge raises
-- "No active usage rate card" and the spend is never recorded.
--
-- Unit note: unlike 001's time-based rows (priced per second), AI_ASSISTANT is metered in
-- TOKENS — billing_worker's suggestion handler passes the combined decide + generate token
-- count as `quantity`. The rate-card lookup does not filter on unit, so this must stay the
-- only current CRD row for AI_ASSISTANT, or the lookup will pick between them arbitrarily.
--
-- Price rationale: a typical inline suggestion costs ~150 tokens across both model calls.
-- At 0.02 CRD/token that settles to 3 credits per suggestion, and the default cap of 15
-- suggestions per meeting bounds the feature at ~45 credits — deliberately small next to
-- STT's 15 credits/minute, since a suggestion is an optional aside rather than the product.
-- calculate_credit_charge rounds up and enforces a 1-credit floor, so no suggestion is free.
-- ADJUST THIS PRICE to match your own pricing model before relying on it commercially.

INSERT INTO subscription.usage_rate_card
    (charge_type, source_language_code, target_language_code,
     unit_price, currency, effective_from, effective_to)
SELECT rate.charge_type, NULL, NULL, rate.unit_price, 'CRD', TIMESTAMPTZ '2026-08-02 00:00:00+00', NULL
FROM (
    VALUES
        ('AI_ASSISTANT'::varchar, 0.020000::decimal(12,6))
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
