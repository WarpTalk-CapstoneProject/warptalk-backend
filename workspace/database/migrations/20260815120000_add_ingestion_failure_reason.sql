-- WT-411 / WT-409: tell "we could not scan it" apart from "it contains sensitive data".
--
-- The guardrail's fail-safe sets confidentiality_level='restricted' AND ingestion_status='failed'
-- on ANY exception — a timeout, a Redis blip, an extraction crash. It also sets 'restricted' when
-- the scan genuinely FINDS PII or DLP content. From the outside those two are the same row, so a
-- document hidden by a transient fault is indistinguishable from one hidden on purpose, and
-- nobody can tell whether retrying would help.
--
-- Nullable and never backfilled: rows that failed before this column existed have no reason on
-- record, and inventing one would be worse than admitting we do not know.
ALTER TABLE workspace.workspace_documents
    ADD COLUMN IF NOT EXISTS ingestion_failure_reason VARCHAR(64);

COMMENT ON COLUMN workspace.workspace_documents.ingestion_failure_reason IS
    'Why the last AI ingestion attempt did not complete: security_scan_failed, embedding_publish_failed, embedding_worker_failed, dlp_detected, pii_unmasked. NULL when ingestion succeeded, was skipped, or predates WT-411.';
