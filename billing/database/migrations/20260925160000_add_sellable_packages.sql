-- Migration: 20260925160000_add_sellable_packages
-- Description:
--   G11 — package management for everything WarpTalk sells besides a base plan.
--
--   WHY
--     Plans were the only sellable row. A top-up was a free-typed credit count priced at
--     credit_value_vnd, there was no way to sell a recurring extra on top of a plan, and no way to
--     discount anything. Admins now manage (/admin/packages):
--
--   1. subscription.credit_packs          one-off bundles of credits (+ bonus), priced per currency,
--                                         with visibility, purchase limits, an active window and an
--                                         optional validity after which unspent credits expire.
--   2. subscription.addons                recurring extras on top of a plan. Each unit raises ONE
--                                         real entitlement key the resolver already produces and a
--                                         consumer already enforces (max_participants,
--                                         max_languages, max_active_rooms, voice_clone,
--                                         ai_assistant, glossary).
--   3. subscription.workspace_addons      a workspace's live add-on subscription (its own Stripe
--                                         subscription, never the plan's).
--   4. subscription.coupons               percent / fixed discounts on plans, packs and add-ons,
--                                         by code or as an auto-apply campaign.
--   5. subscription.coupon_redemptions    one row per paid checkout that used a coupon.
--   6. subscription.credit_pack_purchases one row per paid pack; also where expiry is tracked.
--
--   Stripe: each catalog row carries the Stripe Product / Price ids (stripe_price_ids jsonb, one
--   price per currency or per cycle+currency) the admin sync created. Prices are immutable in
--   Stripe, so a price change creates a new Price and archives the old; nothing is ever deleted.
--
--   Idempotent: IF NOT EXISTS throughout. No BEGIN/COMMIT — the migration runner owns the
--   transaction.

CREATE TABLE IF NOT EXISTS subscription.credit_packs (
    id                     uuid          NOT NULL DEFAULT uuidv7(),
    slug                   varchar(60)   NOT NULL,
    name                   varchar(100)  NOT NULL,
    description            varchar(500)  NULL,
    credits                integer       NOT NULL,
    bonus_credits          integer       NOT NULL DEFAULT 0,
    price_vnd              numeric(14,0) NULL,
    price_usd              numeric(12,2) NULL,
    validity_days          integer       NULL,
    visibility             varchar(20)   NOT NULL DEFAULT 'public',
    eligible_plan_ids      uuid[]        NOT NULL DEFAULT '{}',
    eligible_workspace_ids uuid[]        NOT NULL DEFAULT '{}',
    max_per_workspace      integer       NULL,
    max_total              integer       NULL,
    available_from         timestamptz   NULL,
    available_until        timestamptz   NULL,
    status                 varchar(20)   NOT NULL DEFAULT 'draft',
    sort_order             integer       NOT NULL DEFAULT 0,
    stripe_product_id      varchar(255)  NULL,
    stripe_price_ids       jsonb         NOT NULL DEFAULT '{}'::jsonb,
    stripe_synced_at       timestamptz   NULL,
    stripe_sync_error      varchar(500)  NULL,
    stripe_synced_hash     varchar(64)   NULL,
    created_at             timestamptz   NOT NULL DEFAULT NOW(),
    created_by             uuid          NULL,
    updated_at             timestamptz   NOT NULL DEFAULT NOW(),
    updated_by             uuid          NULL,
    archived_at            timestamptz   NULL,
    CONSTRAINT credit_packs_pkey PRIMARY KEY (id),
    CONSTRAINT ck_credit_packs_credits CHECK (credits > 0),
    CONSTRAINT ck_credit_packs_bonus CHECK (bonus_credits >= 0),
    CONSTRAINT ck_credit_packs_price_vnd CHECK (price_vnd IS NULL OR price_vnd > 0),
    CONSTRAINT ck_credit_packs_price_usd CHECK (price_usd IS NULL OR price_usd > 0),
    CONSTRAINT ck_credit_packs_has_price CHECK (price_vnd IS NOT NULL OR price_usd IS NOT NULL),
    CONSTRAINT ck_credit_packs_validity CHECK (validity_days IS NULL OR validity_days > 0),
    CONSTRAINT ck_credit_packs_visibility CHECK (visibility IN ('public', 'plans', 'workspaces')),
    CONSTRAINT ck_credit_packs_limits CHECK ((max_per_workspace IS NULL OR max_per_workspace > 0) AND (max_total IS NULL OR max_total > 0)),
    CONSTRAINT ck_credit_packs_window CHECK (available_from IS NULL OR available_until IS NULL OR available_from < available_until),
    CONSTRAINT ck_credit_packs_status CHECK (status IN ('draft', 'active', 'archived'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_credit_packs_slug ON subscription.credit_packs (slug);

CREATE TABLE IF NOT EXISTS subscription.addons (
    id                 uuid          NOT NULL DEFAULT uuidv7(),
    slug               varchar(60)   NOT NULL,
    name               varchar(100)  NOT NULL,
    description        varchar(500)  NULL,
    unit_label         varchar(40)   NOT NULL,
    entitlement_key    varchar(50)   NOT NULL,
    units_per_quantity integer       NOT NULL DEFAULT 1,
    price_monthly_vnd  numeric(14,0) NULL,
    price_yearly_vnd   numeric(14,0) NULL,
    price_monthly_usd  numeric(12,2) NULL,
    price_yearly_usd   numeric(12,2) NULL,
    min_quantity       integer       NOT NULL DEFAULT 1,
    max_quantity       integer       NOT NULL DEFAULT 1,
    eligible_plan_ids  uuid[]        NOT NULL DEFAULT '{}',
    status             varchar(20)   NOT NULL DEFAULT 'draft',
    sort_order         integer       NOT NULL DEFAULT 0,
    stripe_product_id  varchar(255)  NULL,
    stripe_price_ids   jsonb         NOT NULL DEFAULT '{}'::jsonb,
    stripe_synced_at   timestamptz   NULL,
    stripe_sync_error  varchar(500)  NULL,
    stripe_synced_hash varchar(64)   NULL,
    created_at         timestamptz   NOT NULL DEFAULT NOW(),
    created_by         uuid          NULL,
    updated_at         timestamptz   NOT NULL DEFAULT NOW(),
    updated_by         uuid          NULL,
    archived_at        timestamptz   NULL,
    CONSTRAINT addons_pkey PRIMARY KEY (id),
    CONSTRAINT ck_addons_entitlement_key CHECK (entitlement_key IN
        ('max_participants', 'max_languages', 'max_active_rooms', 'voice_clone', 'ai_assistant', 'glossary')),
    CONSTRAINT ck_addons_units CHECK (units_per_quantity > 0),
    CONSTRAINT ck_addons_quantity CHECK (min_quantity >= 1 AND max_quantity >= min_quantity),
    CONSTRAINT ck_addons_prices CHECK (
        (price_monthly_vnd IS NULL OR price_monthly_vnd > 0) AND (price_yearly_vnd IS NULL OR price_yearly_vnd > 0)
        AND (price_monthly_usd IS NULL OR price_monthly_usd > 0) AND (price_yearly_usd IS NULL OR price_yearly_usd > 0)),
    CONSTRAINT ck_addons_has_price CHECK (
        price_monthly_vnd IS NOT NULL OR price_yearly_vnd IS NOT NULL
        OR price_monthly_usd IS NOT NULL OR price_yearly_usd IS NOT NULL),
    CONSTRAINT ck_addons_status CHECK (status IN ('draft', 'active', 'archived'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_addons_slug ON subscription.addons (slug);

CREATE TABLE IF NOT EXISTS subscription.workspace_addons (
    id                     uuid          NOT NULL DEFAULT uuidv7(),
    workspace_id           uuid          NOT NULL,
    addon_id               uuid          NOT NULL,
    user_id                uuid          NOT NULL,
    quantity               integer       NOT NULL,
    billing_cycle          varchar(20)   NOT NULL,
    currency               varchar(3)    NOT NULL,
    unit_price             numeric(14,2) NOT NULL,
    amount_billed_total    numeric(16,2) NOT NULL DEFAULT 0,
    status                 varchar(20)   NOT NULL DEFAULT 'active',
    stripe_subscription_id varchar(255)  NULL,
    stripe_session_id      varchar(255)  NULL,
    coupon_id              uuid          NULL,
    current_period_end     timestamptz   NULL,
    started_at             timestamptz   NOT NULL DEFAULT NOW(),
    cancelled_at           timestamptz   NULL,
    created_at             timestamptz   NOT NULL DEFAULT NOW(),
    updated_at             timestamptz   NOT NULL DEFAULT NOW(),
    CONSTRAINT workspace_addons_pkey PRIMARY KEY (id),
    CONSTRAINT fk_workspace_addons_addon FOREIGN KEY (addon_id) REFERENCES subscription.addons (id),
    CONSTRAINT ck_workspace_addons_quantity CHECK (quantity > 0),
    CONSTRAINT ck_workspace_addons_cycle CHECK (billing_cycle IN ('monthly', 'yearly')),
    CONSTRAINT ck_workspace_addons_status CHECK (status IN ('active', 'cancelling', 'cancelled'))
);

CREATE INDEX IF NOT EXISTS ix_workspace_addons_workspace ON subscription.workspace_addons (workspace_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_workspace_addons_stripe_subscription
    ON subscription.workspace_addons (stripe_subscription_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_workspace_addons_stripe_session
    ON subscription.workspace_addons (stripe_session_id);
-- One live row per (workspace, add-on): a second purchase of the same add-on is refused rather
-- than stacked, so quantity is always read from one row.
CREATE UNIQUE INDEX IF NOT EXISTS ux_workspace_addons_one_open
    ON subscription.workspace_addons (workspace_id, addon_id) WHERE status <> 'cancelled';

CREATE TABLE IF NOT EXISTS subscription.coupons (
    id                       uuid          NOT NULL DEFAULT uuidv7(),
    code                     varchar(40)   NULL,
    name                     varchar(100)  NOT NULL,
    discount_type            varchar(20)   NOT NULL,
    percent_off              numeric(5,2)  NULL,
    amount_off               numeric(14,2) NULL,
    amount_off_currency      varchar(3)    NULL,
    applies_to_types         text[]        NOT NULL DEFAULT '{}',
    applies_to_ids           uuid[]        NOT NULL DEFAULT '{}',
    duration                 varchar(20)   NOT NULL DEFAULT 'once',
    duration_in_months       integer       NULL,
    max_redemptions          integer       NULL,
    per_workspace_limit      integer       NOT NULL DEFAULT 1,
    valid_from               timestamptz   NULL,
    valid_until              timestamptz   NULL,
    auto_apply               boolean       NOT NULL DEFAULT false,
    status                   varchar(20)   NOT NULL DEFAULT 'draft',
    stripe_coupon_id         varchar(255)  NULL,
    stripe_promotion_code_id varchar(255)  NULL,
    stripe_synced_at         timestamptz   NULL,
    stripe_sync_error        varchar(500)  NULL,
    stripe_synced_hash       varchar(64)   NULL,
    created_at               timestamptz   NOT NULL DEFAULT NOW(),
    created_by               uuid          NULL,
    updated_at               timestamptz   NOT NULL DEFAULT NOW(),
    updated_by               uuid          NULL,
    archived_at              timestamptz   NULL,
    CONSTRAINT coupons_pkey PRIMARY KEY (id),
    CONSTRAINT ck_coupons_discount CHECK (
        (discount_type = 'percent' AND percent_off IS NOT NULL AND percent_off > 0 AND percent_off <= 100 AND amount_off IS NULL)
        OR (discount_type = 'fixed' AND amount_off IS NOT NULL AND amount_off > 0 AND amount_off_currency IS NOT NULL AND amount_off_currency IN ('vnd', 'usd') AND percent_off IS NULL)),
    CONSTRAINT ck_coupons_duration CHECK (
        (duration = 'repeating' AND duration_in_months IS NOT NULL AND duration_in_months > 0)
        OR (duration IN ('once', 'forever') AND duration_in_months IS NULL)),
    CONSTRAINT ck_coupons_applies_to CHECK (
        cardinality(applies_to_types) > 0 AND applies_to_types <@ ARRAY['plan', 'credit_pack', 'addon']::text[]),
    CONSTRAINT ck_coupons_code_or_auto CHECK (code IS NOT NULL OR auto_apply),
    CONSTRAINT ck_coupons_limits CHECK ((max_redemptions IS NULL OR max_redemptions > 0) AND per_workspace_limit > 0),
    CONSTRAINT ck_coupons_window CHECK (valid_from IS NULL OR valid_until IS NULL OR valid_from < valid_until),
    CONSTRAINT ck_coupons_status CHECK (status IN ('draft', 'active', 'archived'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_coupons_code ON subscription.coupons (code);

CREATE TABLE IF NOT EXISTS subscription.coupon_redemptions (
    id                uuid          NOT NULL DEFAULT uuidv7(),
    coupon_id         uuid          NOT NULL,
    workspace_id      uuid          NOT NULL,
    user_id           uuid          NOT NULL,
    item_type         varchar(20)   NOT NULL,
    item_id           uuid          NULL,
    stripe_session_id varchar(255)  NOT NULL,
    payment_id        uuid          NULL,
    currency          varchar(3)    NOT NULL,
    discount_amount   numeric(14,2) NOT NULL DEFAULT 0,
    redeemed_at       timestamptz   NOT NULL DEFAULT NOW(),
    CONSTRAINT coupon_redemptions_pkey PRIMARY KEY (id),
    CONSTRAINT fk_coupon_redemptions_coupon FOREIGN KEY (coupon_id) REFERENCES subscription.coupons (id),
    CONSTRAINT ck_coupon_redemptions_item_type CHECK (item_type IN ('plan', 'credit_pack', 'addon')),
    CONSTRAINT ck_coupon_redemptions_discount CHECK (discount_amount >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_coupon_redemptions_session
    ON subscription.coupon_redemptions (stripe_session_id);
CREATE INDEX IF NOT EXISTS ix_coupon_redemptions_coupon_workspace
    ON subscription.coupon_redemptions (coupon_id, workspace_id);

CREATE TABLE IF NOT EXISTS subscription.credit_pack_purchases (
    id                uuid          NOT NULL DEFAULT uuidv7(),
    credit_pack_id    uuid          NOT NULL,
    workspace_id      uuid          NOT NULL,
    user_id           uuid          NOT NULL,
    subscription_id   uuid          NOT NULL,
    payment_id        uuid          NULL,
    stripe_session_id varchar(255)  NOT NULL,
    credits           integer       NOT NULL,
    bonus_credits     integer       NOT NULL DEFAULT 0,
    currency          varchar(3)    NOT NULL,
    amount_paid       numeric(14,2) NOT NULL,
    coupon_id         uuid          NULL,
    discount_amount   numeric(14,2) NOT NULL DEFAULT 0,
    purchased_at      timestamptz   NOT NULL DEFAULT NOW(),
    expires_at        timestamptz   NULL,
    expired_credits   integer       NOT NULL DEFAULT 0,
    expired_at        timestamptz   NULL,
    CONSTRAINT credit_pack_purchases_pkey PRIMARY KEY (id),
    CONSTRAINT fk_credit_pack_purchases_pack FOREIGN KEY (credit_pack_id) REFERENCES subscription.credit_packs (id),
    CONSTRAINT ck_credit_pack_purchases_credits CHECK (credits > 0 AND bonus_credits >= 0 AND expired_credits >= 0),
    CONSTRAINT ck_credit_pack_purchases_amount CHECK (amount_paid >= 0 AND discount_amount >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_credit_pack_purchases_session
    ON subscription.credit_pack_purchases (stripe_session_id);
CREATE INDEX IF NOT EXISTS ix_credit_pack_purchases_pack_workspace
    ON subscription.credit_pack_purchases (credit_pack_id, workspace_id);
CREATE INDEX IF NOT EXISTS ix_credit_pack_purchases_due_expiry
    ON subscription.credit_pack_purchases (expires_at) WHERE expired_at IS NULL AND expires_at IS NOT NULL;

COMMENT ON TABLE subscription.credit_packs IS
    'G11: one-off credit bundles sold on top of a plan (/admin/packages). Bought through /payments/checkout (PaymentType CreditPack).';
COMMENT ON TABLE subscription.addons IS
    'G11: recurring extras on top of a plan; each unit raises one enforced entitlement key (EntitlementResolver add-on layer).';
COMMENT ON TABLE subscription.workspace_addons IS
    'G11: a workspace''s add-on subscription, a Stripe subscription of its own. Granting while active, or cancelling before current_period_end.';
COMMENT ON TABLE subscription.coupons IS
    'G11: discounts on plans, credit packs and add-ons, by code or as an auto-apply campaign. One coupon per checkout.';
COMMENT ON TABLE subscription.coupon_redemptions IS
    'G11: paid checkouts that used a coupon, one per Stripe session. The redemption limits count these rows.';
COMMENT ON TABLE subscription.credit_pack_purchases IS
    'G11: paid credit packs, one per Stripe session, written with the ledger grant. CreditPackExpiryWorker removes unspent credits at expires_at.';
COMMENT ON COLUMN subscription.credit_packs.stripe_price_ids IS
    'Current Stripe price per currency, e.g. {"vnd":"price_…","usd":"price_…"}. A price change creates a new Price; the old one is archived, never deleted.';
COMMENT ON COLUMN subscription.addons.stripe_price_ids IS
    'Current Stripe price per cycle and currency, e.g. {"monthly_vnd":"price_…","yearly_usd":"price_…"}.';

DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON
            subscription.credit_packs,
            subscription.addons,
            subscription.workspace_addons,
            subscription.coupons,
            subscription.coupon_redemptions,
            subscription.credit_pack_purchases
        TO warptalk_billing_runtime;
    END IF;
END
$grant$;
