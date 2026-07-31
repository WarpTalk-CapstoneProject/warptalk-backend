-- Migration: 045-29-07-2026-align-billing-invoice-schema
-- Description:
--   Aligns legacy subscription.invoices rows with the BillingService invoice entity.
--   Older local/dev databases used amount/invoice_pdf_url/hosted_invoice_url, while
--   the current payment and workspace billing flows require invoice_number,
--   subtotal/tax/total, line_items, issued_at, due_at, paid_at, and user_id.


ALTER TABLE subscription.invoices
    ADD COLUMN IF NOT EXISTS workspace_id uuid,
    ADD COLUMN IF NOT EXISTS stripe_invoice_id varchar(255),
    ADD COLUMN IF NOT EXISTS amount numeric(12, 2),
    ADD COLUMN IF NOT EXISTS invoice_pdf_url varchar(500),
    ADD COLUMN IF NOT EXISTS hosted_invoice_url varchar(500),
    ADD COLUMN IF NOT EXISTS user_id uuid,
    ADD COLUMN IF NOT EXISTS invoice_number varchar(30),
    ADD COLUMN IF NOT EXISTS subtotal numeric(12, 2),
    ADD COLUMN IF NOT EXISTS tax numeric(12, 2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total numeric(12, 2),
    ADD COLUMN IF NOT EXISTS pdf_url varchar(500),
    ADD COLUMN IF NOT EXISTS line_items jsonb NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN IF NOT EXISTS issued_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS due_at timestamptz,
    ADD COLUMN IF NOT EXISTS paid_at timestamptz;

ALTER TABLE subscription.invoices
    ALTER COLUMN workspace_id DROP NOT NULL,
    ALTER COLUMN stripe_invoice_id DROP NOT NULL,
    ALTER COLUMN amount DROP NOT NULL;

UPDATE subscription.invoices
SET
    invoice_number = COALESCE(
        invoice_number,
        'INV-' || upper(substr(replace(id::text, '-', ''), 1, 12))
    ),
    subtotal = COALESCE(subtotal, amount, 0),
    total = COALESCE(total, amount, 0),
    pdf_url = COALESCE(pdf_url, invoice_pdf_url, hosted_invoice_url),
    issued_at = COALESCE(issued_at, created_at, now()),
    due_at = COALESCE(due_at, created_at + interval '15 days', now() + interval '15 days'),
    paid_at = CASE
        WHEN lower(status) = 'paid' THEN COALESCE(paid_at, created_at, now())
        ELSE paid_at
    END;

ALTER TABLE subscription.invoices
    ALTER COLUMN invoice_number SET NOT NULL,
    ALTER COLUMN subtotal SET NOT NULL,
    ALTER COLUMN total SET NOT NULL,
    ALTER COLUMN due_at SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS invoices_invoice_number_key
    ON subscription.invoices (invoice_number);

CREATE INDEX IF NOT EXISTS ix_invoices_overdue
    ON subscription.invoices (due_at)
    WHERE status <> 'paid';

