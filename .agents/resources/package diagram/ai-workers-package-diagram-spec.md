# Package Diagram Specification - AI Workers (`warptalk-ai`)

The package diagram maps all 13 top-level physical Python package directories under `warptalk-ai/` directly into package boundaries without artificial grouping.

## Physical Python Packages

1. `livekit_ingress_worker`: Real-time audio stream ingress package.
2. `stt_worker`: Speech-to-Text transcription worker package.
3. `translation_worker`: Real-time translation worker package.
4. `tts_worker`: Text-to-Speech audio synthesis worker package.
5. `ai_assistant_worker`: AI Assistant & RAG reasoning worker package.
6. `embedding_worker`: Vector embedding worker package.
7. `security_worker`: Content safety & security worker package.
8. `billing_worker`: Credit usage meter worker package.
9. `metrics_exporter`: Prometheus metrics exporter package.
10. `tools`: Utility scripts & operational tools package.
11. `benchmarks`: Performance benchmarking package.
12. `tests`: Automated test suite package.
13. `shared <<abstract>>`: Core abstract foundation package containing base worker models, schemas, redis client, audio/text utilities, and health probes.

## Dependency Rules

- `livekit_ingress_worker ..> stt_worker`
- `stt_worker ..> translation_worker`
- `translation_worker ..> tts_worker`
- All workers depend on `shared <<abstract>>`.

## Resource Assets

- `.puml` PlantUML source: `ai-workers-package-diagram.puml`
- `.png` Image output: `ai-workers-package-diagram.png`
