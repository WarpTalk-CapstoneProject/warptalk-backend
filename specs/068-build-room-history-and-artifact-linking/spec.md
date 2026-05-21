# WT-68: 1.7 Build Room History and Artifact Linking

## 1. Description
Implement room history tracking and artifact referencing once a translation room session finishes. This feature provides participants with historical access to session outputs such as transcript exports, summary exports, debug logs, recordings, and audio samples while strictly enforcing privacy-sensitive metadata and retention policies.

## 2. Implementation Scope
* **Database Setup & EF Core Mapping**:
  * Register `TranslationRoomArtifact` in `TranslationRoomDbContext` (mapping to the `translation_room.translation_room_artifacts` table).
  * Setup a database migration to create/ensure the `translation_room.translation_room_artifacts` table.
* **Artifact Reference Management**:
  * Implement service-layer logic to link ending rooms to their corresponding artifacts (transcript export, summary export, debug log, optional recording, audio sample).
  * Store artifact metadata: `FileUrl`, `FileFormat`, `FileSizeBytes`, and status.
* **Privacy & Retention Tracking**:
  * Track privacy-sensitive metadata: `ContainsRawAudio` (bool), `ContainsRawVideo` (bool), `ConsentRequired` (bool), and `RetentionUntil` (DateTime?).
* **API Endpoints**:
  * Add API endpoints to view historical rooms and their associated artifacts for users/hosts.
  * Ensure appropriate status filtering (e.g., Ended, Cancelled, Expired).

## 3. Acceptance Criteria
* Ended rooms correctly list and reference all associated artifacts.
* Cancelled and expired rooms appear in history where applicable, while active/draft rooms do not.
* Discarded drafts never persist as room records and thus never appear in history.
* Historical room detail and query APIs return complete artifact metadata (URLs, formats, sizes, and retention deadlines).

* Automated tests verify correct artifact linking, state-based visibility in history, and privacy/retention constraints.

## 4. Output Acceptance (Specify)

**User Story**: As a Participant, I want to review room history and artifacts after a session so that I can access outputs such as transcript exports or summaries.

**Independent Test**: Can be tested independently by ending a active room, creating associated artifact references, then querying the room history and artifact detail APIs.

**Acceptance Scenarios**:

1. **Given** an ended room has transcript or summary artifacts, **When** room history is requested, **Then** the response includes artifact metadata and download links.
2. **Given** an artifact contains raw audio or video, **When** the artifact is persisted, **Then** raw media flags, consent requirement, and retention metadata are successfully stored.
3. **Given** a draft was discarded before creation, **When** room history is queried, **Then** the discarded draft does not appear in the results.

**Functional Requirements**:

* **FR-1.7-001**: System MUST store artifact references for room outputs without owning the external artifact content.
* **FR-1.7-002**: System MUST expose room history and artifacts for rooms that should be visible after lifecycle completion (e.g., `ENDED`, `CANCELLED`, `EXPIRED` statuses).
* **FR-1.7-003**: System MUST track privacy-sensitive metadata including raw audio/video flags, consent requirement, and retention deadline.
* **FR-1.7-004**: System MUST exclude discarded drafts from history.

**Key Entities**: `translation_room.translation_room_artifacts`, `translation_room.translation_rooms`.

**Success Criteria**:
* Room history returns correct artifact metadata.
* Privacy/retention metadata is available and correct for artifacts.
* Tests cover ended room history, artifact visibility, and discarded draft exclusion.

## 5. Resolved Design Decisions & Questions
> [!NOTE]
> Các câu hỏi thiết kế lớn đã được thống nhất phương án xử lý như sau:

1. **Quyền truy cập Lịch sử & Artifact (Access Control)** [RESOLVED]:
   * **Phương án**: Quyền truy cập sẽ phụ thuộc vào tuỳ chỉnh cài đặt (`Settings`) của Host khi tạo/cập nhật phòng Translation Room (ví dụ: cấp quyền xem cho `HostOnly`, `Participants`, hoặc `Workspace`). Việc kiểm tra quyền sẽ được thực hiện thông qua endpoint tải Artifact.
2. **Kiểm soát Truy cập & Tải Artifact qua Gateway** [RESOLVED]:
   * **Thiết kế**: Xây dựng endpoint `/api/v1/room-artifacts/{id}/download` để backend đứng ra kiểm soát việc tải file thay vì trả thẳng link Cloud Storage thô.
   * **Tác dụng**: Giúp backend validate quyền truy cập, kiểm tra thời hạn lưu trữ (`RetentionUntil`), kiểm tra cờ đồng ý (`ConsentRequired`) của user, ẩn hạ tầng lưu trữ thật, và ghi log/audit lịch sử tải file. Cung cấp API chờ user approve consent khi cần thiết.
3. **Cơ chế xử lý khi hết hạn lưu trữ (Retention Lifecycle)** [RESOLVED]:
   * **Thiết kế**: Nếu `DateTime.UtcNow > RetentionUntil`, hệ thống sẽ không trả về link tải file (`FileUrl`) và đánh dấu trạng thái Artifact động là `Expired`.
   * **Xử lý vật lý**: Việc xóa file vật lý trên Cloud Storage sẽ do cơ chế Lifecycle Policy của Cloud tự động xử lý. Backend sẽ không tự động Hard-delete bản ghi DB trừ khi có chính sách đặc biệt yêu cầu.
4. **Dòng chảy tạo Artifact (Creation Flow)** [RESOLVED]:
   * **Thiết kế**: Thực hiện tự động và bất đồng bộ bởi `ArtifactsFinalizationBackgroundService` thông qua lắng nghe sự kiện hoàn tất từ Redis Pub/Sub của các AI Python Workers. Không cần API bên ngoài gọi vào để tạo bản ghi.



