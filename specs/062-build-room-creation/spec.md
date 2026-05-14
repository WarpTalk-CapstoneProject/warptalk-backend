# Feature Specification: 1.1 & 1.2 Build Room Creation and Access Flow

**Feature Branch**: `feature/translation-room`  
**Created**: 2026-05-13  
**Status**: Completed  
**Input**: Linear Ticket WT-62, WT-63

## Description

Implement the backend feature for creating instant and scheduled translation rooms, as well as the flow for joining a translation room as host or member.

## User Scenarios & Testing *(mandatory)*

**User Story (WT-62)**: As a Host, I want to create an instant or scheduled translation room so that participants have a valid room context before translation starts.
**User Story (WT-63)**: As a Participant, I want to join a valid translation room so that I can participate in the realtime session.

**Independent Test**: Can be tested independently by calling the create room API with valid and invalid payloads to generate a room code, and subsequently calling the join API as host/member to verify participant record creation or rejection.

**Acceptance Scenarios**:

1. **Given** a host submits a valid scheduled room request, **When** the API processes the request, **Then** the system persists a room with status `SCHEDULED` and a unique room code.
2. **Given** a host submits a valid instant room request, **When** the API processes the request, **Then** the system persists a room with status `WAITING` and returns the room context.
3. **Given** a valid room code and joinable room status, **When** a participant joins, **Then** the system creates or updates a participant record and returns room plus participant context.
4. **Given** a room is `ENDED`, `CANCELLED`, or `EXPIRED`, **When** a participant tries to join, **Then** the system rejects the join with a clear error.

## Requirements *(mandatory)*

### Business Rules & Constraints

- **BR-001**: Chỉ được vào phòng (join) khi `translation_room_code` hợp lệ.
- **BR-002**: Chỉ được join khi phòng đang ở trạng thái hợp lệ để tham gia (active/waiting).
- **BR-003**: Không được join nếu phòng ở một trong các trạng thái: `ENDED`, `CANCELLED`, `EXPIRED`.
- **BR-004**: Hệ thống phải kiểm tra quyền truy cập trước khi cho join.
- **BR-005**: Nếu không đủ quyền, participant phải bị đánh dấu hoặc trả về trạng thái `REJECTED`.
- **BR-006**: Hệ thống phải tạo mới hoặc cập nhật participant record khi người dùng join thành công.
- **BR-007**: Trạng thái participant trong luồng join phải được quản lý đúng theo các giá trị: `INVITED`, `WAITING`, `CONNECTED`, `REJECTED`.
- **BR-008**: API join phải trả về đủ context của room và participant để client vào phòng được.
- **BR-009**: A user who was previously REJECTED by host is allowed to join again and will re-enter the room with status WAITING.
- **BR-010**: A user who was previously KICKED is permanently blocked from joining the room again.

### Functional Requirements

- **FR-1.1-001**: System MUST create scheduled and instant translation rooms for an authenticated host.
- **FR-1.1-002**: System MUST validate workspace, host, title, source language, target languages, max participants, and schedule time before persistence.
- **FR-1.1-003**: System MUST generate a unique `translation_room_code` (VARCHAR 12) and prevent code collision.
- **FR-1.1-004**: System MUST set initial status according to room type: `SCHEDULED` for scheduled rooms and `WAITING` for instant rooms.
- **FR-1.2-001**: System MUST fulfill all business rules (BR-001 to BR-008) during the join flow.

### Technical Constraints & Standards

- **TC-001**: Cần chấp hành tất cả các code convention và architecture của hệ thống.
- **TC-002**: Code theo đúng chuẩn Clean Architecture hiện tại của hệ thống.

### Key Entities

- `translation_room.translation_rooms`
- `translation_room.translation_room_participants`
- `platform.supported_languages` (if applicable)

## Success Criteria *(mandatory)*

- Valid create requests return a room ID/code.
- Valid participants can join active/waiting rooms.
- Closed or invalid rooms cannot be joined.
- Automated tests cover scheduled creation, instant creation, validation failure, valid join, and closed room.

## Technical Debt

- **TD-001**: Validation for `max_participants` limit based on workspace subscription tiers.
- **TD-002**: Validation for the maximum allowed scheduling time in the future for a `SCHEDULED` room.

## Implementation Notes

- Fully implemented the Create Translation Room and Join Translation Room flows with DTOs, Mappers, and Service Logic.
- Adheres strictly to Clean Architecture. The Presentation layer (gRPC/Controllers) correctly routes through the Application layer (`ITranslationRoomService`) without leaking Infrastructure implementation details.
- Integrated `FluentValidation` to cover all edge cases (6/6 tests passing for `CreateTranslationRoomRequestValidator`).
- Adopted `Enum` structures (`TranslationRoomParticipantRole`, `TranslationRoomParticipantStatus`, `RoomStatus`) rather than magic strings to enhance type safety, leveraging `HasConversion` in EF Core to ensure PostgreSQL compatibility.
- Centralized `TranslationRoomConstants` by flattening nested error definitions and aligning with project conventions (e.g., `NotificationConstants`).
- Automated Testing is fully operational with 8/8 test cases passing for validators and helpers.
