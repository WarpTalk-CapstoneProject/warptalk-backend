-- Migration: 018-add-sales-scoring-weights
-- Description:
--   Seeds the four sales-scoring weights read by UsageRateCardAdminService
--   (sales_usage_weight, sales_members_weight, sales_languages_weight,
--   sales_ai_services_weight).
--
--   These four rows were originally added to 016-add-billing-policy-config.sql
--   after that migration had already been applied to production. The runner
--   checksums every applied migration and aborts the whole service chain when
--   the content changes underneath it, which is what it did — correctly. An
--   applied migration is immutable; new rows belong in a new file.
--
--   Idempotent via ON CONFLICT DO NOTHING, so this is safe on a database that
--   somehow already carries the keys and on one that does not.

INSERT INTO subscription.billing_pricing_config (key, value)
VALUES
    ('sales_usage_weight', 0.45),
    ('sales_members_weight', 0.15),
    ('sales_languages_weight', 0.15),
    ('sales_ai_services_weight', 0.25)
ON CONFLICT (key) DO NOTHING;
