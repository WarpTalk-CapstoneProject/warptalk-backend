# Package Diagram Specification - WarpTalk System

The system-level package diagram maps the current WarpTalk implementation boundary, top-level repository boundaries, and package directories that physically exist in the workspace.

## Visual Style

- The diagram has no PlantUML `title`; the largest package container is labeled `WarpTalk System`.
- Concrete leaf package tabs are left blank; package names are centered inside the main body rectangle.
- Abstract contract package tabs use `<<abstract>>`, with package names centered inside the main body rectangle.
- Container package names are shown in the top-left tab because their member packages are displayed inside the container.

## System boundary package

- `WarpTalk System`: Outer container for the internal implementation packages: `Client Repositories`, `warptalk-backend`, `warptalk-ai`, and `warptalk-infrastructure`.

## Real package boundaries reflected in the diagram

### Client repositories

- `warptalk-web`
- `warptalk-desktop`

### Backend repository

- Concrete module directories: `gateway`, `auth`, `workspace`, `meeting`, `translation-room`, `transcript`, `assistant`, `billing`, `notification`
- Shared contract packages: `shared <<abstract>>`, `contracts <<abstract>>`

### AI runtime repository

- Worker/runtime packages: `livekit_ingress_worker`, `stt_worker`, `translation_worker`, `tts_worker`, `ai_assistant_worker`, `embedding_worker`, `security_worker`, `billing_worker`, `metrics_exporter`
- Additional runtime package: `suggestion_worker`
- Shared contract package: `AI shared <<abstract>>`

### Infrastructure repository

- Operational packages: `terraform`, `deploy`, `observability`, `pgbouncer`, `coturn`

## Dependency summary

- Client repositories depend on `warptalk-backend`.
- `warptalk-backend` depends on `warptalk-ai` through backend-owned Redis Streams; no separate `Redis Streams` package is drawn because it is not a standalone package boundary.
- `warptalk-infrastructure` contains deployment, networking, and observability packages. It is shown as an internal package boundary, but no runtime dependency/call arrows are drawn from infrastructure to backend or AI.
