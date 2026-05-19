# Module 2 Integration Flow Coverage (WT-78)

## Primary Journey

1. Capture speech chunk enters runtime pipeline (`stt:results:{roomId}`).
2. Transcript segment persistence writes stable business data (`transcript.transcript_segments`).
3. Translation result processing writes translated output (`transcript.transcript_translations`).
4. Transcript fetch endpoints expose original and translated segments.
5. Correction endpoint writes correction audit and updates corrected content.
6. Re-translation result updates translation records idempotently.
7. Export endpoint creates and retrieves transcript exports.

## Runtime Boundary Rule

- Runtime states such as buffering, STT processing, translation processing, retry, and broadcasting are pipeline states.
- PostgreSQL persists stable outputs (segments, translations, corrections, exports), not transient runtime stage transitions.

## Verification Mapping

- API route contract coverage: `transcript/tests/WarpTalk.TranscriptService.IntegrationTests/Module2ApiContractTests.cs`
- Runtime boundary assertions: `transcript/tests/WarpTalk.TranscriptService.IntegrationTests/Module2RuntimeBoundaryTests.cs`
- Service contract reference: `transcript/docs/module-2-api-realtime-contract.md`
