# Module 2 API and Realtime Contract

## Scope

This document defines the REST surface and runtime boundaries for Module 2 (Transcript Service).

References:
- `exports/warptalk-schema-updated.dbml`
- `exports/warptalk-module-2-state-diagrams.md`

## Runtime Boundary

- Redis Streams and realtime processing represent transient runtime states.
- PostgreSQL persists stable business outputs only:
  - `transcript.transcripts`
  - `transcript.transcript_segments`
  - `transcript.transcript_translations`
  - `transcript.transcript_corrections`
  - `transcript.transcript_exports`
- High-frequency runtime transitions are not modeled as persisted stage rows.

## REST Endpoints (Module 2)

### Transcript Query

- `GET /api/v1/transcripts/{id}`: fetch transcript metadata by transcript id.
- `GET /api/v1/transcripts/by-room/{translationRoomId}`: fetch transcript by translation room reference.

### Segment and Translation Query

- `GET /api/v1/transcripts/{transcriptId}/segments?skip=&take=`: paged original segment retrieval.
- `GET /api/v1/transcripts/{transcriptId}/translations?skip=&take=`: paged translated segment retrieval.

### Correction Flow

- `POST /api/v1/transcripts/{transcriptId}/segments/{segmentId}/correct`
  - Request body: `CreateCorrectionDto`
  - Purpose: submit corrected text for a segment.
  - Major errors: `401` unauthorized caller, `403` participant without permission, `404` transcript/segment not found, `400` invalid correction payload.
- `GET /api/v1/transcripts/{transcriptId}/segments/{segmentId}/corrections`
  - Purpose: view correction history for one segment.

### Export Flow

- `POST /api/v1/transcripts/{transcriptId}/exports`
  - Request body: `CreateTranscriptExportRequest`
  - Purpose: create transcript export artifact.
- `GET /api/v1/transcripts/{transcriptId}/exports/{id}/download`
  - Purpose: download export file bytes.

### Glossary Support

- `POST /api/v1/glossaries`
- `GET /api/v1/glossaries/{id}`
- `GET /api/v1/glossaries/workspace/{workspaceId}`
- `PUT /api/v1/glossaries/{id}`
- `DELETE /api/v1/glossaries/{id}`
- `POST /api/v1/glossaries/{id}/terms`
- `GET /api/v1/glossaries/{id}/terms`
- `PUT /api/v1/glossaries/{id}/terms/{termId}`
- `DELETE /api/v1/glossaries/{id}/terms/{termId}`

## Realtime Notes

- Transcript Service consumes:
  - `stt:results:{roomId}` for segment persistence.
  - `translate:results:{roomId}` for translation persistence.
- Gateway and frontend consume broadcast-ready data downstream; Transcript Service focuses on durable persistence and query APIs.
