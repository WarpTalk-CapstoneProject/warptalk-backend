# Package Diagram Specification - AI Workers (`warptalk-ai`)

The package diagram maps all 14 top-level physical Python package directories under `warptalk-ai/` inside one outer `warptalk-ai` package boundary, without artificial grouping.

## Visual Style

- The diagram has no PlantUML `title`; the repository boundary is represented by the outer `warptalk-ai` package label.
- The outer `warptalk-ai` package is the largest package boundary and surrounds every AI worker, support, test, and shared package.
- Concrete package tabs are left blank; package names are centered inside the main body rectangle.
- The abstract `shared` package uses `<<abstract>>` in the tab, with `shared` centered inside the main body rectangle.
- Dependency arrows are orthogonal, use standard open V arrowheads, and keep clearance from package borders.

## Physical Python Packages

1. `livekit_ingress_worker`: Real-time audio stream ingress package.
2. `stt_worker`: Speech-to-Text transcription worker package.
3. `translation_worker`: Real-time translation worker package.
4. `tts_worker`: Text-to-Speech audio synthesis worker package.
5. `ai_assistant_worker`: AI Assistant & RAG reasoning worker package.
6. `embedding_worker`: Vector embedding worker package.
7. `suggestion_worker`: Inline transcript suggestion worker package.
8. `security_worker`: Content safety & security worker package.
9. `billing_worker`: Credit usage meter worker package.
10. `metrics_exporter`: Prometheus metrics exporter package.
11. `tools`: Utility scripts & operational tools package.
12. `benchmarks`: Performance benchmarking package.
13. `tests`: Automated test suite package.
14. `shared <<abstract>>`: Core abstract foundation package containing base worker models, schemas, redis client, audio/text utilities, and health probes.

## Dependency Rules

- All runtime, operational, benchmark, and test packages depend on `shared <<abstract>>` for base worker models, Redis contracts, schemas, config, and utilities.
- The diagram groups those repeated shared-foundation dependencies into one summarized arrow into `shared <<abstract>>` to avoid duplicate arrowheads.
- No runtime direct worker-to-worker dependency arrows are drawn.

Runtime communication is intentionally not drawn as direct package-to-package dependency arrows. The AI workers communicate indirectly through Redis Streams / Redis keys:

- `livekit_ingress_worker` publishes `audio:chunks`; `stt_worker` consumes it for transcription, and `tts_worker` consumes it for voice-clone buffering.
- `stt_worker` publishes `stt:results`; `translation_worker`, `ai_assistant_worker`, and `suggestion_worker` consume it through separate consumer groups.
- `translation_worker` publishes `translate:results`; `tts_worker` consumes it for dubbing, and `billing_worker` consumes it for billing settlement.
- `tts_worker` publishes `tts:results`; `billing_worker` consumes it for billing settlement.
- `ai_assistant_worker` publishes `embedding:search_requests` and reads `embedding:search_result:*`; `embedding_worker` handles the semantic-search request/reply over Redis.

## Resource Assets

- `.puml` PlantUML source: `ai-workers-package-diagram.puml`
- `.png` Image output: `ai-workers-package-diagram.png`, regenerated from the title-free PUML with the outer `warptalk-ai` package boundary.
