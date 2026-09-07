# Refactor: WT-603 Meeting Runtime Cleanup

Date: 2026-09-03

## What is being refactored

- Meeting Service Polls, Q&A, and Breakouts vertical slices.
- Legacy RTC entity, repository, navigation, and unit-of-work names.
- Meeting Service PostgreSQL migration set and its staged infrastructure copy.
- Web live-room code that still references the retired features.

## Why

The retired features widen the active product and persistence surface without a
current user workflow. The remaining C# names also conflict with the deployed
RTC table terminology and blur the boundary between Meeting Runtime and the
business meeting source of truth in Translation Room Service.

## What does not change

- Live room join, participant presence, host controls, chat, recording, and
  transcript behavior.
- Public contracts for retained Meeting Service functionality.
- Database `warptalk_meeting`, schema `meeting`, and existing RTC table/column
  names.
- Service-to-service communication channels and deployment topology.

Removing Polls, Q&A, and Breakouts is an approved product-scope reduction, not
a behavior-preserving rename. The retained runtime behavior must remain
identical before and after the refactor.

## Constitution compliance check

- [x] Clean Architecture boundaries remain Domain -> Application -> Infrastructure -> API.
- [x] gRPC, Redis Streams, SignalR, and LiveKit channels for retained behavior are unchanged.
- [x] PostgreSQL remains behind the existing PgBouncer/deployment configuration.
- [x] The destructive schema change is versioned, staged, and release-gated.
- [x] Meeting Service tests pass before and after implementation.
- [x] Web typecheck/build and retained live-room contract tests pass.
- [x] Infrastructure migration coverage passes and production deployment check is documented as blocked locally by missing `jq`.
