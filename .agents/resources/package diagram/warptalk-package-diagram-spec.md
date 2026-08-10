# Package Diagram Specification - WarpTalk System

The system-level package diagram maps the current top-level repository boundaries and package directories that physically exist in the workspace.

## Real package boundaries reflected in the diagram

### Client repositories

- `warptalk-web`
- `warptalk-desktop`

### Backend repository

- Concrete module directories: `gateway`, `auth`, `workspace`, `meeting`, `translation-room`, `transcript`, `assistant`, `billing`, `notification`, `payment`
- Shared contract packages: `shared <<abstract>>`, `contracts <<abstract>>`

### AI runtime repository

- Worker/runtime packages: `livekit_ingress_worker`, `stt_worker`, `translation_worker`, `tts_worker`, `ai_assistant_worker`, `embedding_worker`, `security_worker`, `billing_worker`, `metrics_exporter`
- Shared contract package: `shared <<abstract>>`

### Infrastructure repository

- Operational packages: `terraform`, `deploy`, `observability`, `pgbouncer`, `coturn`

## Dependency summary

- Client repositories depend on `warptalk-backend`.
- `warptalk-backend` orchestrates `warptalk-ai`.
- `warptalk-infrastructure` provisions and operates both backend and AI runtime layers.
