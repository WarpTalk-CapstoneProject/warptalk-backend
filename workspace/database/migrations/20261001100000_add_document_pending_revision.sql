-- WT-854 (part 2): a corrected file for a PUBLISHED document waits in its own slot until approved.
--
-- "Upload a corrected version" (WT-633) overwrote storage_key and the file's metadata the moment it
-- was uploaded, and sent the document back to pending_approval. So the moment somebody uploaded a
-- replacement, readers lost the approved document, and a rejection could not bring it back: the
-- approved file's key survived only in the audit row's metadata.
--
-- The replacement now goes into the pending_* columns below and storage_key keeps pointing at the
-- approved file, which is the only one readers, downloads and the AI index ever see. Approval
-- promotes the pending values into the live columns (and re-indexes); rejection deletes the pending
-- object and clears them. pending_storage_key is the marker: all pending_* columns are NULL
-- together when nothing is waiting.
--
-- A REJECTED document still takes a replacement in place, as before — there is no approved file
-- for anybody to keep reading.
--
-- Nullable, no backfill: no document has a pending revision until this ships. Idempotent, and no
-- BEGIN/COMMIT — the migration runner owns the transaction.
ALTER TABLE workspace.workspace_documents
    ADD COLUMN IF NOT EXISTS pending_storage_key VARCHAR(500),
    ADD COLUMN IF NOT EXISTS pending_storage_provider VARCHAR(50),
    ADD COLUMN IF NOT EXISTS pending_name VARCHAR(255),
    ADD COLUMN IF NOT EXISTS pending_file_name VARCHAR(255),
    ADD COLUMN IF NOT EXISTS pending_file_extension VARCHAR(20),
    ADD COLUMN IF NOT EXISTS pending_mime_type VARCHAR(100),
    ADD COLUMN IF NOT EXISTS pending_size_bytes BIGINT,
    ADD COLUMN IF NOT EXISTS pending_content_hash VARCHAR(64),
    ADD COLUMN IF NOT EXISTS pending_note VARCHAR(2000),
    ADD COLUMN IF NOT EXISTS pending_uploaded_by UUID,
    ADD COLUMN IF NOT EXISTS pending_uploaded_at TIMESTAMPTZ;

COMMENT ON COLUMN workspace.workspace_documents.pending_storage_key IS
    'WT-854: storage key of a corrected file awaiting review for a published document. NULL when '
    'nothing is pending. Readers always use storage_key; approval promotes this, rejection deletes '
    'the object and clears every pending_* column.';

COMMENT ON COLUMN workspace.workspace_documents.pending_uploaded_by IS
    'External AuthService user id of whoever uploaded the pending revision. No physical FK.';
