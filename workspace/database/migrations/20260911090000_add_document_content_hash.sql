-- WT-666: give a document's BYTES an identity, so the same file uploaded twice can be recognised.
--
-- Upload had no notion of content at all. Re-uploading the same file produced a second document
-- row, a second encrypted blob and a second full set of AI chunks in the vector store, with
-- nothing on either row to say they were the same file — so the assistant answered from two copies
-- and the library listed two entries the uploader could not tell apart.
--
-- The hash is of the PLAINTEXT, computed at upload time before encryption. It cannot be derived
-- from what is already stored: the blob is AES-256-CBC with a per-save random IV, so the same file
-- encrypted twice is two different byte strings on disk.
--
-- Nullable and deliberately NOT backfilled. Backfilling would mean decrypting every existing
-- document, and a row whose hash we do not know must not be silently treated as a duplicate of
-- anything — an unknown hash simply never matches, which is the honest answer.
ALTER TABLE workspace.workspace_documents
    ADD COLUMN IF NOT EXISTS content_hash VARCHAR(64);

COMMENT ON COLUMN workspace.workspace_documents.content_hash IS
    'Lowercase hex SHA-256 of the document file''s plaintext bytes, set at upload. NULL for rows that predate WT-666; a NULL hash never matches a duplicate check.';

-- The duplicate check is always "this workspace, this hash", so the workspace id leads. Not
-- UNIQUE: WT-666 lets a member deliberately keep a second copy (`create_new`), and a unique
-- constraint would turn that choice into a 500 from the database.
CREATE INDEX IF NOT EXISTS idx_workspace_documents_workspace_content_hash
    ON workspace.workspace_documents (workspace_id, content_hash);
