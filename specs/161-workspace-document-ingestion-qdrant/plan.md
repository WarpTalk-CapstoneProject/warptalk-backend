# Workspace Document Ingestion to Qdrant Plan

## Goal
Make document ingestion end-to-end truthful:
- security worker returns real scan results
- embedding worker returns real indexing results
- backend persists those results
- Qdrant receives vectors for eligible documents
- UI only shows `completed` after the full pipeline finishes

## Current Gap
1. Backend marks ingestion `completed` too early.
2. Security scan result is returned by the worker, but the backend does not yet surface it in a durable way.
3. Embedding result is published by worker, but backend does not consume it yet.
4. UI only sees existing document columns, so stage-level detail must be derived from existing fields and latest worker results.
5. RabbitMQ exists in infrastructure but is deferred as tech debt for this flow; current implementation uses Redis Streams end to end.

## Plan

### 1. Keep the scaffolded entity unchanged
Do not add new document columns to the scaffolded entity.
Use the existing document fields as the source of truth:
- `AiEligible`
- `IsAiAllowed`
- `IngestionStatus`
- `ConfidentialityLevel`
- `LastIndexedAt`
- `IndexVersion`

If stage detail is needed, carry it through worker result payloads and backend projections, not the entity.

### 2. Update backend security flow
Change `DocumentSecurityGuardrailConsumerService` so it:
- sets `IngestionStatus = processing` while scan/index is running
- waits for `security_worker` result
- stores the security result in `WorkspaceDocumentAudit.Metadata`
- updates `ConfidentialityLevel`
- computes `AiEligible`
- publishes `embedding:index_requests` only when the document is approved and AI eligible
- leaves eligible documents in `processing` until the embedding result is consumed

### 3. Add backend consumer for embedding results
Create a hosted service that consumes `embedding:index_results` and:
- finds the document by `source_id`
- stores the latest embedding result in `WorkspaceDocumentAudit.Metadata`
- updates `LastIndexedAt`
- writes `IndexVersion`
- recomputes `AiEligible`
- updates `IngestionStatus`

### 4. Define status rules
- `completed` only when security is done and embedding worker confirms Qdrant indexing
- `failed` when a technical error prevents the pipeline from finishing
- `skipped` when policy/security prevents indexing
- `processing` while either stage is still running

### 5. Update access rules
Adjust document access checks so RAG usage depends on:
- `AiEligible = true`
- `IngestionStatus = completed`
- `LastIndexedAt` present
- document not deleted

### 6. Update DTOs and UI
Expose the existing fields plus derived ingestion labels so the UI can show:
- scanning
- indexing
- indexed
- blocked
- failed

### 7. Add observability
Log and trace:
- `document_id`
- `scan_id`
- `embedding_job_id`
- worker status
- chunk count
- qdrant collection name

### 8. Verify with an E2E checklist
Use these checks:
- clean document -> scan pass -> embedding indexed -> Qdrant point exists
- PII document -> restricted -> no Qdrant write
- DLP document -> restricted -> no Qdrant write
- large document -> multiple chunks -> multiple vectors in Qdrant

## Acceptance Criteria
- backend does not mark ingestion complete before embedding finishes
- security worker result is persisted
- embedding worker result is persisted
- Qdrant collection exists for eligible documents
- semantic search can retrieve the indexed chunks

## Notes
- RabbitMQ durable orchestration is tech debt for a later infrastructure pass
- production remains fail-closed for security failures
- embedding worker must still publish a result even when indexing is blocked or failed
