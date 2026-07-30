-- Billing-owned commercial plan baseline.
-- Keep stable IDs for demo/test references; ON CONFLICT preserves plans that
-- operators have already customized through the Billing admin API.
INSERT INTO subscription.plans (
    id,
    name,
    slug,
    tier,
    price,
    currency,
    billing_cycle,
    credits_per_cycle,
    max_participants,
    max_languages,
    voice_clone_enabled,
    ai_assistant_enabled,
    glossary_enabled,
    dedicated_gpu,
    features,
    sort_order,
    is_active,
    created_at,
    updated_at
)
VALUES
    (
        '019ec641-9776-7d50-b2b9-9edb93a46d23',
        'Startup',
        'startup',
        'Startup',
        190000,
        'VND',
        'monthly',
        30000,
        15,
        2,
        true,
        true,
        false,
        false,
        '{"voice_clone_limit_mins": 120}'::jsonb,
        1,
        true,
        now(),
        now()
    ),
    (
        '019ec641-9776-7d50-b2b9-9edb93a46d24',
        'Enterprise',
        'enterprise',
        'Enterprise',
        490000,
        'VND',
        'monthly',
        100000,
        100,
        10,
        true,
        true,
        true,
        true,
        '{"voice_clone_limit_mins": -1}'::jsonb,
        2,
        true,
        now(),
        now()
    )
ON CONFLICT (slug) DO NOTHING;
