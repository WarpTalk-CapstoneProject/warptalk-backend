-- Migration: 20260925180000_add_operating_expenses
-- Ticket: G12 — internal management: operating costs and expenses (/admin/finance/expenses)
-- Created At: 2026-09-25
-- Description:
--   What the company spends to run WarpTalk that is not a per-usage provider cost: servers and VPS,
--   domains, SaaS subscriptions, provider plan fees, salaries and contractors, marketing, other.
--   Revenue and AI provider cost already exist (admin Insights P&L); these rows are the third term
--   that turns gross margin into a net result.
--
--   1. subscription.expense_categories  — the managed category list (seeded with seven).
--   2. subscription.operating_expenses  — one row per expense. Amount in its own currency (VND or
--      USD); VND is NOT stored: every report converts a USD row at the USD→VND rate of its own
--      expense_date from subscription.fx_rates, like the P&L does, so an FX correction reaches
--      every past figure. A row whose recurrence is monthly or yearly is also a series: the
--      billing ExpenseRecurrenceWorker writes each next occurrence as a 'planned' row (linked by
--      recurring_source_id) a few days before next_due_date and advances next_due_date. The unique
--      index on (recurring_source_id, expense_date) makes two replicas generating the same
--      occurrence harmless.
--   3. subscription.expense_budgets     — one budget per (category, month), in VND.
--
--   Idempotent: IF NOT EXISTS / ON CONFLICT DO NOTHING. No BEGIN/COMMIT — the migration runner owns
--   the transaction.

CREATE TABLE IF NOT EXISTS subscription.expense_categories (
    id          uuid          NOT NULL DEFAULT gen_random_uuid(),
    slug        varchar(60)   NOT NULL,
    name        varchar(120)  NOT NULL,
    description varchar(500)  NULL,
    color       varchar(20)   NULL,
    sort_order  integer       NOT NULL DEFAULT 0,
    is_active   boolean       NOT NULL DEFAULT true,
    created_at  timestamptz   NOT NULL DEFAULT NOW(),
    updated_at  timestamptz   NOT NULL DEFAULT NOW(),
    updated_by  uuid          NULL,
    CONSTRAINT expense_categories_pkey PRIMARY KEY (id),
    CONSTRAINT ck_expense_categories_slug CHECK (slug ~ '^[a-z0-9][a-z0-9_]*$')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_expense_categories_slug
    ON subscription.expense_categories (slug);

COMMENT ON TABLE subscription.expense_categories IS
    'G12: categories of operating expenses, managed in /admin/finance/expenses. Retired with is_active = false, never deleted while an expense uses one.';

CREATE TABLE IF NOT EXISTS subscription.operating_expenses (
    id                    uuid           NOT NULL DEFAULT gen_random_uuid(),
    expense_date          date           NOT NULL,
    vendor                varchar(200)   NOT NULL,
    category_id           uuid           NOT NULL,
    description           varchar(2000)  NULL,
    amount                numeric(18,2)  NOT NULL,
    currency              varchar(3)     NOT NULL,
    payment_method        varchar(40)    NULL,
    status                varchar(20)    NOT NULL DEFAULT 'paid',
    paid_at               timestamptz    NULL,
    paid_by               varchar(200)   NULL,
    tags                  text[]         NOT NULL DEFAULT '{}',
    recurrence            varchar(20)    NOT NULL DEFAULT 'none',
    next_due_date         date           NULL,
    recurrence_end_date   date           NULL,
    recurring_source_id   uuid           NULL,
    receipt_storage_key   varchar(400)   NULL,
    receipt_file_name     varchar(255)   NULL,
    receipt_content_type  varchar(120)   NULL,
    receipt_size_bytes    bigint         NULL,
    import_batch_id       uuid           NULL,
    created_by            uuid           NULL,
    updated_by            uuid           NULL,
    created_at            timestamptz    NOT NULL DEFAULT NOW(),
    updated_at            timestamptz    NOT NULL DEFAULT NOW(),
    deleted_at            timestamptz    NULL,
    CONSTRAINT operating_expenses_pkey PRIMARY KEY (id),
    CONSTRAINT operating_expenses_category_id_fkey
        FOREIGN KEY (category_id) REFERENCES subscription.expense_categories (id),
    CONSTRAINT operating_expenses_recurring_source_id_fkey
        FOREIGN KEY (recurring_source_id) REFERENCES subscription.operating_expenses (id),
    CONSTRAINT ck_operating_expenses_amount CHECK (amount >= 0),
    CONSTRAINT ck_operating_expenses_currency CHECK (currency IN ('VND', 'USD')),
    CONSTRAINT ck_operating_expenses_status CHECK (status IN ('planned', 'paid')),
    CONSTRAINT ck_operating_expenses_recurrence CHECK (recurrence IN ('none', 'monthly', 'yearly')),
    CONSTRAINT ck_operating_expenses_series CHECK (
        (recurrence = 'none' AND next_due_date IS NULL)
        OR (recurrence <> 'none' AND next_due_date IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS ix_operating_expenses_date
    ON subscription.operating_expenses (expense_date)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_operating_expenses_category
    ON subscription.operating_expenses (category_id);

CREATE INDEX IF NOT EXISTS ix_operating_expenses_series_due
    ON subscription.operating_expenses (next_due_date)
    WHERE recurrence <> 'none' AND deleted_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_operating_expenses_occurrence
    ON subscription.operating_expenses (recurring_source_id, expense_date)
    WHERE recurring_source_id IS NOT NULL;

COMMENT ON TABLE subscription.operating_expenses IS
    'G12: company operating expenses (not per-usage provider cost). Amount in its own currency; reports convert USD at the expense_date rate of subscription.fx_rates. Soft-deleted with deleted_at.';
COMMENT ON COLUMN subscription.operating_expenses.next_due_date IS
    'Series only (recurrence monthly|yearly): the date of the next occurrence not yet written. Advanced by ExpenseRecurrenceWorker.';
COMMENT ON COLUMN subscription.operating_expenses.recurring_source_id IS
    'An occurrence written by ExpenseRecurrenceWorker: the series row it came from.';
COMMENT ON COLUMN subscription.operating_expenses.receipt_storage_key IS
    'Object key of the uploaded receipt in the billing receipt bucket (Storage:S3). NULL = no receipt.';

CREATE TABLE IF NOT EXISTS subscription.expense_budgets (
    id          uuid           NOT NULL DEFAULT gen_random_uuid(),
    category_id uuid           NOT NULL,
    month       date           NOT NULL,
    amount_vnd  numeric(18,2)  NOT NULL,
    note        varchar(500)   NULL,
    created_at  timestamptz    NOT NULL DEFAULT NOW(),
    updated_at  timestamptz    NOT NULL DEFAULT NOW(),
    updated_by  uuid           NULL,
    CONSTRAINT expense_budgets_pkey PRIMARY KEY (id),
    CONSTRAINT expense_budgets_category_id_fkey
        FOREIGN KEY (category_id) REFERENCES subscription.expense_categories (id),
    CONSTRAINT ck_expense_budgets_amount CHECK (amount_vnd >= 0),
    CONSTRAINT ck_expense_budgets_month CHECK (EXTRACT(DAY FROM month) = 1)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_expense_budgets_category_month
    ON subscription.expense_budgets (category_id, month);

COMMENT ON TABLE subscription.expense_budgets IS
    'G12: monthly budget per expense category in VND. month is the first day of the month.';

DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_billing_runtime') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.expense_categories TO warptalk_billing_runtime;
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.operating_expenses TO warptalk_billing_runtime;
        GRANT SELECT, INSERT, UPDATE, DELETE ON subscription.expense_budgets TO warptalk_billing_runtime;
    END IF;
END
$grant$;

INSERT INTO subscription.expense_categories (slug, name, description, color, sort_order)
VALUES
    ('servers', 'Servers & hosting', 'VPS, cloud servers, storage and bandwidth.', 'blue', 10),
    ('domains', 'Domains & DNS', 'Domain registrations, DNS and certificates.', 'cyan', 20),
    ('saas', 'SaaS subscriptions', 'GitHub, Linear, Canva and other tools.', 'violet', 30),
    ('provider_plans', 'Provider plan fees', 'Cartesia, LiveKit and OpenAI plans or prepaid balances (not per-usage cost).', 'amber', 40),
    ('salaries', 'Salaries & contractors', 'Payroll, freelancers and contractors.', 'green', 50),
    ('marketing', 'Marketing', 'Ads, events and design.', 'pink', 60),
    ('other', 'Other', 'Anything else.', 'gray', 70)
ON CONFLICT (slug) DO NOTHING;
