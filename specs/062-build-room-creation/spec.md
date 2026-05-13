# Feature Specification: 1.1 Build Room Creation and Scheduling Flow

**Feature Branch**: `feature/translation-room`  
**Created**: 2026-05-13  
**Status**: Approved  
**Input**: Linear Ticket WT-62

## Description

Implement the backend feature for creating instant and scheduled translation rooms.

## User Scenarios & Testing *(mandatory)*

**User Story**: As a Host, I want to create an instant or scheduled translation room so that participants have a valid room context before translation starts.

**Independent Test**: Can be tested independently by calling the create room API with valid and invalid payloads, then verifying the persisted room, generated code, initial status, and validation errors.

**Acceptance Scenarios**:

1. **Given** a host submits a valid scheduled room request, **When** the API processes the request, **Then** the system persists a room with status `SCHEDULED`, schedule metadata, language policy, and a unique room code.
2. **Given** a host submits a valid instant room request, **When** the API processes the request, **Then** the system persists a room with status `WAITING` and returns the room context needed for participants to join.
3. **Given** a host discards a draft before submission, **When** no create request is committed, **Then** no draft room record is persisted.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-1.1-001**: System MUST create scheduled and instant translation rooms for an authenticated host.
- **FR-1.1-002**: System MUST validate workspace, host, title, source language, target languages, max participants, and schedule time before persistence. (Schedule time MUST be strictly greater than `now`).
- **FR-1.1-003**: System MUST generate a unique `translation_room_code` (VARCHAR 12) and prevent code collision.
- **FR-1.1-004**: System MUST set initial status according to room type: `SCHEDULED` for scheduled rooms and `WAITING` for instant rooms.

### Technical Constraints & Standards

- **TC-001**: Cần chấp hành tất cả các code convention và architecture của hệ thống.
- **TC-002**: Define các enums ở file constants, mappers và helpers theo đúng chuẩn của hệ thống.

### Key Entities

- `translation_room.translation_rooms`
- `platform.supported_languages` (if applicable)
- external `workspace_id`
- external `host_id`

## Success Criteria *(mandatory)*

- Valid create requests return a room ID/code and can be fetched afterward.
- Invalid create requests fail without partial room persistence.
- Automated tests cover scheduled creation, instant creation, validation failure, and room code uniqueness.

## Technical Debt

- **TD-001**: Validation for `max_participants` limit based on workspace subscription tiers.
- **TD-002**: Validation for the maximum allowed scheduling time in the future for a `SCHEDULED` room.
