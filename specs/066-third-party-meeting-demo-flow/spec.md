# Feature Specification: Third-Party Meeting Demo Flow (WT-132)

**Feature Branch**: `docs/wt-132-third-party-meeting-demo-flow`  
**Created**: 2026-05-20  
**Status**: Draft  
**Input**: Linear issue `WT-132` - "6.12 Prepare third-party meeting demo flow"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Host can prepare demo devices and routing (Priority: P1)

As a demo host, I need a deterministic device setup for real mic, virtual speaker/cable, virtual mic output, and local speaker output so that the demo can run without ad-hoc troubleshooting.

**Why this priority**: Incorrect device routing is the main failure mode for virtual-audio demos.

**Independent Test**: Follow setup checklist on a clean machine and verify all four devices map to expected input/output roles before opening the meeting app.

**Acceptance Scenarios**:

1. **Given** required virtual-audio drivers are installed, **When** the host follows the setup checklist, **Then** all required device roles are assigned without ambiguity.
2. **Given** wrong device selection, **When** host runs the pre-demo checks, **Then** the checklist surfaces mismatch before live demo starts.

---

### User Story 2 - Host can demonstrate bidirectional translation (Priority: P1)

As a demo host, I need a scripted Vietnamese -> English and English -> Vietnamese flow so stakeholders can verify translation behavior in both directions.

**Why this priority**: Core acceptance for Module 6 requires two-way translation proof.

**Independent Test**: Execute scripted lines in both directions and validate expected spoken output plus transcript updates.

**Acceptance Scenarios**:

1. **Given** demo session is active, **When** User A speaks Vietnamese line, **Then** remote participant receives English output and transcript shows source + translated segments.
2. **Given** remote participant speaks English line, **When** session is active, **Then** User A receives Vietnamese output and transcript shows source + translated segments.

---

### User Story 3 - Host can recover from common failures (Priority: P2)

As a demo host, I need a limitation and fallback playbook so I can recover quickly when permissions, devices, or latency degrade.

**Why this priority**: Demo reliability matters more than perfect path.

**Independent Test**: Simulate at least one device/permission failure and follow fallback path to continue demo.

**Acceptance Scenarios**:

1. **Given** virtual device is disconnected, **When** host follows fallback steps, **Then** demo can continue with alternate device mapping or transcript-only mode.
2. **Given** translation latency spikes, **When** host applies fallback script, **Then** stakeholders still observe bidirectional pipeline behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System documentation MUST define one supported demo platform for Sprint 2 walkthrough (Zoom selected for baseline).
- **FR-002**: System documentation MUST define explicit role mapping for these audio paths: real mic input, virtual speaker/cable capture, virtual mic output, and real speaker/headphones output.
- **FR-003**: Demo script MUST include at least one Vietnamese -> English and one English -> Vietnamese scripted utterance with expected outcomes.
- **FR-004**: Demo flow MUST include transcript validation checkpoints (source text + translated text visibility).
- **FR-005**: Demo flow MUST include pre-demo checks for device selection, permissions, and route sanity.
- **FR-006**: Demo flow MUST document known limitations and at least one fallback procedure per failure category (device, permission, latency/pipeline).

### Edge Cases

- Host machine has multiple similarly named virtual devices and selects the wrong one.
- Meeting platform auto-switches audio device after reconnect.
- OS microphone permission is revoked mid-demo.
- STT/TTS pipeline is delayed but still functional.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new team member can complete setup checklist in 15 minutes or less without external support.
- **SC-002**: Bidirectional scripted flow completes with transcript visible for both directions in one dry run.
- **SC-003**: At least one simulated failure can be recovered with the documented fallback in 3 minutes or less.

## Assumptions

- Sprint 2 scope prioritizes demo readiness over production-hardening of all edge cases.
- Virtual audio driver availability differs by OS; this spec focuses on reproducible process, not OS automation.
- Existing Module 2 transcript pipeline and MeetingService foundations are available in current branch baseline.
