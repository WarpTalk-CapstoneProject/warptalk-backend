-- Migration: 044-28-07-2026-add-sales-inquiries
-- Description:
--   Stores landing-page pricing inquiries so system admins can review customer
--   needs and convert them into workspace-specific Enterprise contracts.


CREATE TABLE IF NOT EXISTS subscription.sales_inquiries (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    first_name varchar(80) NOT NULL,
    last_name varchar(80) NOT NULL,
    work_email varchar(255) NOT NULL,
    company varchar(160) NOT NULL,
    request_type varchar(80) NOT NULL,
    feature_interests jsonb NOT NULL DEFAULT '[]'::jsonb,
    target_languages jsonb NOT NULL DEFAULT '[]'::jsonb,
    current_monthly_meeting_volume varchar(80) NOT NULL,
    expected_monthly_meeting_volume_in_six_months varchar(80),
    use_case_notes text,
    pricing_estimate_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    consent boolean NOT NULL,
    source varchar(80) NOT NULL DEFAULT 'landing_pricing',
    status varchar(30) NOT NULL DEFAULT 'new',
    workspace_id uuid,
    subscription_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    converted_at timestamptz,
    closed_at timestamptz,
    CONSTRAINT sales_inquiries_status_chk
        CHECK (status IN ('new', 'reviewing', 'quoted', 'converted', 'closed'))
);

CREATE INDEX IF NOT EXISTS ix_sales_inquiries_status
    ON subscription.sales_inquiries (status);

CREATE INDEX IF NOT EXISTS ix_sales_inquiries_work_email
    ON subscription.sales_inquiries (work_email);

CREATE INDEX IF NOT EXISTS ix_sales_inquiries_workspace_id
    ON subscription.sales_inquiries (workspace_id);

CREATE INDEX IF NOT EXISTS ix_sales_inquiries_created_at
    ON subscription.sales_inquiries (created_at DESC);

COMMENT ON COLUMN subscription.sales_inquiries.workspace_id IS 'External WorkspaceService workspace id. No physical FK.';
COMMENT ON COLUMN subscription.sales_inquiries.subscription_id IS 'Billing subscription id captured after conversion. Kept nullable for pre-workspace inquiries.';

