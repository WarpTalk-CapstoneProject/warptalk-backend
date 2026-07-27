# Document Preview and MinIO Storage Implementation Plan

## Goal

Implement **document preview** and the app-level **MinIO/S3 storage configuration** for the workspace module without adding new database tables and without storing document content in PostgreSQL.

## Scope

- Keep PostgreSQL as metadata, access control, and audit storage only.
- Preview must use the existing `workspace.workspace_documents` record.
- Do not create a separate preview artifact table or preview file record.
- Do not store raw document content in the database.
- Support preview for PDF, image, and text-like files first.
- Support a shared MinIO/S3 config path that can be reused by multiple modules.

## Storage Direction

- Use MinIO or S3 as the object storage layer for workspace documents.
- Keep `AmazonS3Client` as the integration client.
- Use pre-signed URL flow for upload and download where possible.
- Move bucket provisioning to infrastructure/provisioning, not runtime `EnsureBucketExistsAsync()`.
- Keep backend responsible for auth, metadata, and access policy.
- Keep browser responsible for direct file retrieval when a signed URL is returned.

## Task List

### 1. Confirm preview policy

- Define preview behavior per file type.
- Define fallback behavior for unsupported files.
- Define access rules that preview must share with download.

### 2. Review current storage contract

- Inspect `IWorkspaceDocumentStorage`.
- Confirm support for:
  - pre-signed GET URL
  - read stream access
  - object metadata access if needed
- Remove any assumptions that preview needs local filesystem storage.

### 2a. Review current MinIO/S3 wiring

- Inspect workspace infrastructure dependency injection for storage provider selection.
- Confirm `Storage:Provider` supports local, S3, and MinIO paths.
- Confirm MinIO config values are sourced from app configuration.
- Confirm the current `storage_provider` mapping matches the real runtime provider.
- Fix any hardcoded local-storage mapper behavior.

### 2b. Define shared cloud config for multi-module use

- Define one shared MinIO/S3 config shape for all modules that store files.
- Keep environment-specific values in app settings or secrets.
- Keep bucket names logical and module-scoped.
- Reuse the same client setup pattern across modules instead of duplicating custom clients.

### 3. Add backend preview service

- Create a dedicated preview service in the workspace application layer.
- Resolve preview mode from `mime_type` and `file_extension`.
- Enforce workspace membership and document ACL checks before any preview response.
- Return either:
  - signed URL for browser-native preview
  - text preview payload for text-like documents
  - unsupported response for DOC/legacy cases

### 4. Define preview response contract

- Add a preview DTO or response model.
- Include fields such as:
  - `mode`
  - `contentType`
  - `fileName`
  - `previewUrl`
  - `textPreview`
  - `truncated`
  - `reason`

### 5. Add preview endpoint

- Add a dedicated preview endpoint in the workspace document controller.
- Keep download endpoint separate.
- Map auth failures to 403 and missing document to 404.
- Return unsupported preview gracefully instead of failing the request.

### 5a. Use pre-signed URL flow

- For PDF and image preview, return signed URLs instead of proxying content through backend.
- For download, return signed URLs where the storage policy allows it.
- Keep the backend as the permission gate and metadata source.
- Keep object storage as the file transfer source.

### 6. Implement file-type routing

- PDF: return signed URL for browser PDF preview.
- Image: return signed URL for direct image rendering.
- TXT/MD/CSV/JSON: read from storage and return text preview.
- DOCX: extract text in memory and return text preview.
- DOC: mark unsupported or download-only.

### 7. Harden security

- Use short-lived signed URLs.
- Sanitize any text preview output.
- Do not log signed URLs.
- Enforce the same workspace/document policy used by download.

### 7a. Production bucket provisioning

- Provision buckets in infrastructure for production.
- Do not rely on runtime bucket creation in production paths.
- Keep runtime bucket creation only for local/dev convenience if still needed.
- Ensure bucket names and IAM/policy settings are consistent across environments.

### 8. Update frontend document UI

- Call the preview endpoint from the document detail page.
- Render preview based on the returned mode.
- Show loading, denied, unsupported, and truncated states.
- Keep download available as a separate action.

### 9. Verify database usage

- Confirm `workspace_documents` continues to store only metadata.
- Confirm no new preview-related table is introduced.
- Confirm no document content is persisted in PostgreSQL.

### 9a. Verify workspace module schema fit

- Keep `workspace_documents` for file metadata only.
- Keep `workspace_document_access_policies` for ACL.
- Keep `workspace_document_audits` for traceability.
- Do not add storage-content columns to PostgreSQL for preview or source file content.

### 10. Add tests

- Add unit tests for preview mode routing.
- Add tests for permission failure and not-found cases.
- Add tests for PDF/image signed URL responses.
- Add tests for text extraction fallback.
- Add frontend tests for preview rendering states.
- Add tests for MinIO/S3 provider selection and storage-provider mapping.
- Add tests for download behavior after switching to signed-url flow.

## Done Criteria

- Preview works for PDF, image, and text-like documents.
- Unsupported types fall back cleanly.
- PostgreSQL still stores metadata only.
- Download and preview remain separate flows.
- No new database table is added.
- MinIO/S3 config is centralized enough to be reused by other modules.
