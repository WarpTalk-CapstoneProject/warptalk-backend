# Feature Specification: Enterprise Workspace & External Collaboration (WT-157)

**Feature Branch**: `feat/workspace-external-collaboration`  
**Created**: 2026-06-02  
**Status**: Approved  
**Input**: Linear ticket WT-157 - Workspace business rules for Enterprise and External Collaboration  

---

## 1. Problem Statement

As WarpTalk expands into Enterprise models, tổ chức (organizations) cần quản lý chặt chẽ ranh giới dữ liệu và thành viên. Khách hàng doanh nghiệp muốn mời đối tác, vendor (External Collaborator) tham gia cuộc họp nhưng KHÔNG muốn họ có quyền truy cập vào danh bạ nội bộ hoặc các transcript/tài liệu nhạy cảm của toàn công ty.

Để giải quyết bài toán này, hệ thống Workspace cần phân định rõ ràng giữa **Internal Member** và **External Member**:
1. **Internal Member**: Bắt buộc khớp với Verified Company Domain và bị giới hạn chỉ được nằm trong **1** Enterprise Workspace duy nhất để bảo vệ tính toàn vẹn dữ liệu của tổ chức.
2. **External Member**: (VD: dùng @gmail.com, @yahoo.com) Có thể được mời vào **nhiều** Enterprise Workspace khác nhau, tuy nhiên quyền hạn bị giới hạn cực kỳ nghiêm ngặt và chỉ tham gia dưới sự cho phép của Admin.
3. **Data Isolation (RBAC)**: `External Member` không có quyền quản trị, không xem được danh bạ (member directory), và **chỉ được xem/tải tài nguyên của các cuộc họp (meeting) mà họ trực tiếp tham gia**.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Phân loại và Giới hạn số lượng Enterprise Workspace
Quy tắc kiểm tra (Validation Rule) khi user đồng ý lời mời vào Enterprise Workspace:
- **Nếu user là Internal Member** (Email khớp với `VerifiedDomains` của workspace):
  - Hệ thống kiểm tra xem user này đã thuộc một `Enterprise` workspace nào khác chưa.
  - **Action**: Reject (`403 Forbidden` hoặc `400 Bad Request`) nếu vi phạm. Một account Internal chỉ thuộc 1 Enterprise Workspace.
- **Nếu user là External Member** (Email KHÔNG khớp `VerifiedDomains`, ví dụ email cá nhân):
  - User được phép tham gia **nhiều** Enterprise Workspace.
  - Tuy nhiên, role bắt buộc phải là `External Member` (không bao giờ được làm Internal role như Admin/Owner).

### 2.2. Domain Verification & Invite Validation
- Bổ sung cấu hình `VerifiedDomains` (dạng List<string>, ví dụ: `["fpt.edu.vn", "warptalk.vn"]`) và cờ `AllowExternalCollaboration` (boolean) vào `WorkspaceConfiguration`.
- Khi gọi API `/invite`:
  - Lấy phần domain của email được mời.
  - Nếu domain **thuộc** `VerifiedDomains`: Xử lý bình thường (Internal Member).
  - Nếu domain **không thuộc** `VerifiedDomains`:
    - Check `AllowExternalCollaboration == false` ➔ **REJECT** (`403 Forbidden`).
    - Check `AllowExternalCollaboration == true` ➔ Bắt buộc gán `RoleId` tương ứng với `External Member` (bỏ qua Role mà Admin truyền lên).

### 2.3. Cấp quyền & Hạn chế của External Member
- Bổ sung hệ thống Role mới: `External Member` (bên cạnh Owner, Admin, Member).
- **Admin Control**: External Member KHÔNG được phép thay đổi settings, quản lý domain, hay tạo lời mời (invite) cho Internal Member.
- **Directory Access**: External Member bị giới hạn khi gọi API `GET /members`. Họ chỉ được phép xem danh sách contact của các **Workspace Admin** (và Owner) để liên hệ khi cần, không được xem danh bạ toàn bộ internal members của workspace.
- **Resource Scoping (Transcript/Artifact)**: Tại `TranslationRoomService` và `TranscriptService`, sửa lại Policy. Nếu role là `External Member`, bắt buộc phải join với bảng `MeetingParticipants` để kiểm tra `UserId` có tồn tại trong meeting đó hay không trước khi trả về dữ liệu View/Download/Export.

---

## 3. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - Enterprise Workspace Limit (Internal vs External)
*Là hệ thống, tôi muốn đảm bảo nhân viên nội bộ chỉ thuộc 1 Enterprise Workspace, nhưng đối tác có thể tham gia nhiều Workspace.*
1. **Given** Internal User A (`a@fpt.com`) đang là member của Enterprise Workspace "FPT",  
   **When** A nhận được invite từ Enterprise Workspace "Vingroup" (verify domain: vingroup.com) và bấm Accept,  
   **Then** hệ thống REJECT với lỗi "Tài khoản Internal của bạn đã thuộc về một Enterprise Workspace khác."
2. **Given** External User B (`b@gmail.com`) đang là External Member của "FPT",  
   **When** B nhận được invite từ "Vingroup" và bấm Accept,  
   **Then** hệ thống ACCEPT và thêm B vào "Vingroup" dưới role `External Member`.

### User Story 2 - External Collaboration Validation
*Là một admin, tôi muốn hệ thống chặn các lời mời ra ngoài tổ chức khi tính năng External Collaboration bị tắt.*
1. **Given** workspace có `VerifiedDomains = ["warptalk.vn"]` và `AllowExternalCollaboration = false`,  
   **When** Admin mời `guest@gmail.com`,  
   **Then** hệ thống REJECT lỗi "Workspace hiện không cho phép mời External Member."
2. **Given** `AllowExternalCollaboration = true`,  
   **When** Admin mời `guest@gmail.com` với role `Admin`,  
   **Then** hệ thống vẫn tự động giáng xuống role `External Member` và gửi invite.

### User Story 3 - Resource Isolation cho External Member
*Là một external member, tôi chỉ có thể xem nội dung các buổi họp tôi được mời tham gia.*
1. **Given** User X là External Member,  
   **When** X gọi API xem danh sách Member của toàn bộ workspace,  
   **Then** hệ thống REJECT `403 Forbidden`.
2. **Given** Meeting M1 (X có tham gia) và Meeting M2 (X không tham gia),  
   **When** X gọi API lấy Transcript của M1, **Then** `200 OK`.  
   **When** X gọi API lấy Transcript của M2, **Then** `403 Forbidden`.

---

## 4. Requirements

- **FR-157-001**: Hệ thống MUST chặn `Internal Member` tham gia >1 Enterprise Workspace.
- **FR-157-002**: Hệ thống MUST cho phép `External Member` tham gia nhiều Enterprise Workspace.
- **FR-157-003**: Hệ thống MUST từ chối email ngoài domain nếu `AllowExternalCollaboration` = false.
- **FR-157-004**: Email ngoài domain MUST bị ép thành role `External Member`, không được cấp quyền quản trị.
- **FR-157-005**: `External Member` MUST bị chặn quyền truy cập Workspace Settings và chỉ được xem contact của Workspace Admin/Owner trong Member Directory.
- **FR-157-006**: `External Member` MUST chỉ được View/Download/Export các tài nguyên (meeting, transcript, artifact) mà họ là participant.

---

## 5. Security & Regression Risks
- Rủi ro lỗ hổng khi kiểm tra `MeetingParticipants`: Cần đảm bảo logic query DB không bị bypass bằng cách truyền thiếu tham số.
- Đảm bảo logic check `VerifiedDomains` sử dụng **exact domain equality** (khớp tuyệt đối) by default. (Ví dụ: `fpt.edu.vn` sẽ KHÔNG tự động bao gồm `sv.fpt.edu.vn`).
