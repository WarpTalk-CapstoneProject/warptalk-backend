# Feature Specification: WT-78 Module 2 Integration Tests and API Docs

**Feature Branch**: `test/wt-78-module-2-integration-tests-api-docs`  
**Created**: 2026-05-20  
**Status**: Draft  
**Input**: Linear WT-78

## User Story

As the development team, we need a repeatable integration verification harness and clear API/realtime contracts so Module 2 implementation can be validated consistently in local runs and CI.

## Acceptance Scenarios

1. **Given** Module 2 backend changes, **When** integration tests run, **Then** test artifacts verify the speak -> STT segment -> translation -> correction -> re-translation -> export path coverage.
2. **Given** frontend/integration developers need service contracts, **When** they read Module 2 docs, **Then** they can identify REST endpoints, payload expectations, realtime boundary rules, and major error cases.
3. **Given** runtime pipeline states are high-frequency, **When** tests/docs are reviewed, **Then** they explicitly confirm transient runtime states are not persisted as high-frequency PostgreSQL state transitions.

## Functional Requirements

- **FR-001**: Provide a runnable `dotnet test` integration harness for Module 2 contract coverage.
- **FR-002**: Provide Module 2 API and realtime contract documentation with endpoint purpose, request/response summary, and error cases.
- **FR-003**: Document local and CI commands for running the harness.
- **FR-004**: Keep docs aligned with `exports/warptalk-schema-updated.dbml` and `exports/warptalk-module-2-state-diagrams.md` runtime boundaries.

## Success Criteria

- `dotnet test` succeeds for the WT-78 integration harness project.
- Documentation includes all main transcript endpoints and correction/export flow.
- Verification artifacts explicitly cover runtime state persistence boundaries.
