# Feature Specification: Document Management & Access Policy (WT-158)

**Feature Branch**: `feat/workspace-document-access-policy`  
**Created**: 2026-06-05  
**Status**: Approved  
**Input**: Linear ticket WT-158 - Workspace Document Access Policy & External Member Isolation Rules

---

## 1. Problem Statement & Context

### 1.1. Problem Statement
Hệ thống quản lý tài liệu trong WarpTalk đòi hỏi cơ chế bảo mật nghiêm ngặt để bảo vệ thông tin nội bộ của doanh nghiệp (Enterprise), đồng thời vẫn cho phép cộng tác linh hoạt với đối tác bên ngoài (External Member). 

Để thực hiện điều này, hệ thống cần giải quyết các bài toán sau:
1. **Precedence (Quy tắc ưu tiên)**: Khi một người dùng đồng thời khớp với nhiều chính sách phân quyền trái ngược nhau (ví dụ: được cho phép theo Role nhưng bị cấm theo MembershipType), hệ thống phải có quy tắc nhất quán để xử lý xung đột.
2. **Default Action (Phân quyền mặc định)**: Khi không tìm thấy bất kỳ chính sách nào chỉ định quyền truy cập cho người dùng.
3. **External Member Boundaries (Ranh giới của đối tác ngoài)**: External Member mặc định không được phép truy cập bất kỳ tài nguyên nào trong Workspace (bao gồm tài liệu, cuộc họp, sensitive data) trừ khi có liên quan trực tiếp đến cuộc họp họ được mời tham gia trong một khoảng thời gian nhất định.
4. **Database Schema Design (Thiết kế cơ sở dữ liệu)**: Thiết kế bảng cấu hình chính sách truy cập tài liệu (`WorkspaceDocumentAccessPolicies`) sao cho linh hoạt, tối ưu và không bị phình to khi hệ thống mở rộng các tiêu chí phân quyền trong tương lai.

### 1.2. User Story
> **As an** enterprise workspace member,  
> **I want to** upload, organize, and access internal documents,  
> **So that** meetings and AI features can use approved company knowledge safely.

### 1.3. B2B Direction
Tài liệu thuộc quyền sở hữu của **Enterprise Workspace**, không thuộc về tài khoản cá nhân của người dùng. Mọi quyền truy cập (Access Control), chính sách lưu trữ (Retention Policy) và chính sách khai thác AI (AI Usage Policy) phải tuân theo tư cách thành viên (Workspace Membership) và chính sách bảo mật của doanh nghiệp.

---

## 2. Product Scope & Functional Requirements

### 2.1. Scope (Phạm vi)
* **Metadata & Storage Contract**: Bổ sung metadata lưu trữ tài liệu trong Workspace, tách biệt thông tin Metadata trong DB và tệp tin vật lý lưu trữ trên Storage Provider (S3/MinIO/Local Storage).
* **Document Lifecycle**: Hỗ trợ các hành động: Upload, List, Search, View/Download, Archive/Delete và kiểm tra quyền truy cập (Permission check).
* **Metadata Tracking**: Theo dõi chủ sở hữu tài liệu (`document.owner_id` / `uploaded_by`), loại tài liệu (`document_type`), kích thước (`size_bytes`), nguồn (`source_type`), trạng thái lưu trữ (`retention_state`) và audit logs (IP, UserAgent, Actor).
* **Allowed File Types (Sprint 1)**: Giới hạn các định dạng tệp tin cho phép tải lên bao gồm: **PDF, DOCX, TXT** và các tài nguyên xuất ra từ cuộc họp (meeting artifacts như transcript, minutes).
* **AI Boundary**: Thiết lập ranh giới tài liệu để chuẩn bị cho các tính năng AI trong tương lai (RAG, Summarization, Contextual Retrieval). Các tài liệu đã bị xóa hoặc đưa vào trạng thái lưu trữ (Archived) **bắt buộc không được phép** sử dụng làm ngữ cảnh cho AI.

### 2.2. Business Rules (Quy tắc Nghiệp vụ)
* Tài liệu chỉ hiển thị đối với những thành viên có thẩm quyền thuộc Workspace của doanh nghiệp.
* Quản trị viên (Workspace Admin/Owner) có quyền quản lý toàn bộ tài liệu; Thành viên thông thường (Workspace Member) truy cập dựa trên chính sách được gán.
* Tài liệu nhạy cảm (`is_sensitive = true`) bắt buộc phải ghi lại lịch sử nhật ký (Audit Trail) khi được tải lên, truy cập (View/Download) và xóa bỏ.
* Các định dạng tệp tin không được hỗ trợ hoặc vượt quá dung lượng quy định phải bị từ chối với mã lỗi thích hợp.

---

## 3. Approved Technical Decisions (Quyết định kỹ thuật đã phê duyệt)

Tất cả các đề xuất đặc tả đã được phê duyệt ngày 2026-06-05:

### 3.1. Quy tắc độ ưu tiên (Evaluation Order / Precedence)
Hệ thống áp dụng thuật toán **Deny-Overrides (Cấm ghi đè cho phép)**. 
* Nếu có **bất kỳ** chính sách nào trả về `DENY` khớp với định danh/thuộc tính của người dùng, quyền truy cập sẽ bị **từ chối ngay lập tức**, bất kể có bao nhiêu chính sách `ALLOW` khác tồn tại.
* Thứ tự đánh giá logic:
  $$\text{Access} = \text{Match(DENY)} ? \text{DENIED} : (\text{Match(ALLOW)} ? \text{ALLOWED} : \text{DEFAULT\_ACTION})$$

### 3.2. Phân quyền mặc định (Default Action) & Ranh giới External Member
* **Đối với tài liệu nhạy cảm (`is_sensitive = true`):** Áp dụng **Deny-by-default**. Nếu không khớp bất kỳ chính sách nào, quyền truy cập bị cấm.
* **Đối với tài liệu đang trong quá trình xử lý AI (`ingestion_status = 'pending'`):** Áp dụng nguyên tắc **Security-first**. Chỉ có **Workspace Owner/Admin** và **Document Owner** (`document.owner_id`) mới được phép truy cập xem/đọc hoặc tải tài liệu. Các thành viên nội bộ khác (Internal Member) và đối tác ngoài (External Member) đều bị chặn truy cập cho đến khi quá trình phân loại hoàn tất và trạng thái chuyển thành `'completed'`.
* **Đối với tài liệu thông thường (`is_sensitive = false` và `ingestion_status = 'completed'`):**
  * **Internal Member:** Mặc định cho phép đọc (`ALLOW` by default), trừ khi có chính sách cấm cụ thể.
  * **External Member:** Vẫn bị cấm mặc định. Chỉ được xem nếu có chính sách cho phép cụ thể hoặc thuộc ngoại lệ cuộc họp.
* **Ngoại lệ cuộc họp đối với External Member:**
  * Chỉ được phép xem tài liệu thuộc cuộc họp mà họ là Participant trực tiếp.
  * **Grace Period:** Thời gian hiệu lực mặc định là 24 giờ (cấu hình trong `appsettings.json`) kể từ khi kết thúc cuộc họp, và cho phép ghi đè trong `workspaces.settings` bằng key `"ExternalGracePeriodHours"`.

### 3.3. Quyền quản trị chính sách (Access Policy Administration)
* Chỉ những đối tượng sau mới được phép thực hiện CRUD các bản ghi `WorkspaceDocumentAccessPolicy` cho một tài liệu:
  * Người dùng có vai trò là **Workspace Owner** hoặc **Workspace Admin**.
  * Người sở hữu tài liệu đó (khớp `document.owner_id` với `UserId` của người yêu cầu).

### 3.4. Cơ chế thiết lập thuộc tính `IsSensitive` cho Tài liệu
Giá trị `is_sensitive` của thực thể tài liệu được quyết định và kiểm soát như sau:
1. **Lúc tải lên (Upload Document):**
   * Người upload (Document Owner) hoặc Workspace Admin có quyền gắn cờ `IsSensitive` thông qua payload yêu cầu (`UploadDocumentRequest`).
   * Giá trị mặc định nếu không truyền lên là `false` (tài liệu thông thường).
2. **Quét nội dung tự động bằng AI (Auto-Classification):**
   * Sau khi tài liệu được upload lên Storage thành công, tiến trình chạy ngầm (Background Job) thực hiện tiền xử lý tài liệu (RAG Ingestion) sẽ quét nội dung văn bản.
   * Nếu phát hiện thông tin nhạy cảm (như thông tin cá nhân PII, thẻ tín dụng, mã nguồn doanh nghiệp, API keys, mật khẩu...), AI Service sẽ tự động cập nhật cờ `is_sensitive = true` để nâng mức độ bảo vệ.
3. **Cập nhật thủ công sau đó (Update Metadata):**
   * Document Owner hoặc Workspace Owner/Admin có quyền cập nhật thủ công cờ `IsSensitive` bất cứ lúc nào thông qua API `PATCH /api/v1/workspaces/{workspaceId}/documents/{documentId}`.

---

## 4. Database Schema Design (WT-158)

### 4.1. Phân tích phương án thiết kế cho `WorkspaceDocumentAccessPolicy`

Hiện tại, thực thể `WorkspaceDocumentAccessPolicy` có cấu trúc:
* `string SubjectType` (ví dụ: `"Role"`, `"MembershipType"`, `"User"`)
* `Guid? SubjectId` (dùng cho định danh Guid cụ thể như `UserId`)
* `string? RoleKey` (đang lưu chuỗi như `"Admin"`, `"Member"`, ...)

Chúng ta cần lựa chọn giữa hai phương án thiết kế để mở rộng cấu hình chính sách:

#### Phương án A: Polymorphic Subject (Đổi tên `RoleKey` thành `SubjectKey`)
* **Cách hoạt động**: Đổi tên (hoặc dùng alias) cột `RoleKey` thành `SubjectKey` (kiểu `string`).
  * Khi `SubjectType = "Role"`, `SubjectKey` lưu giá trị role (`"Member"`, `"Admin"`).
  * Khi `SubjectType = "MembershipType"`, `SubjectKey` lưu giá trị (`"Internal"`, `"External"`).
  * Khi `SubjectType = "User"`, hệ thống sử dụng `SubjectId` chứa `UserId`.
* **Ưu điểm**:
  * **Extensible (Dễ mở rộng)**: Nếu tương lai phát sinh thêm các tiêu chí phân quyền mới (ví dụ: `UserGroup`, `Department`, `ServiceAccount`), ta chỉ cần thêm các giá trị `SubjectType` mới mà không cần chạy migration thay đổi cấu trúc bảng database.
  * **Normalized & Simple**: DB schema gọn gàng, mỗi dòng policy đại diện cho đúng một điều kiện lọc duy nhất (Single Responsibility).
  * **Độ tương thích cao**: Phù hợp với các engine phân quyền chuẩn (ABAC/RBAC) như AWS IAM, Kubernetes RBAC.
* **Nhược điểm**: Muốn áp dụng quy tắc kết hợp nhiều thuộc tính (ví dụ: vừa phải là `Member` vừa phải là `External`), ta phải cấu hình nhiều dòng policy khác nhau và dựa vào engine tính toán kết hợp (Deny-Overrides).

#### Phương án B: Multi-column Attributes (Tách thành 2 cột `RoleKey` và `MembershipType`)
* **Cách hoạt động**: Bảng chứa cả hai cột độc lập `RoleKey` và `MembershipType`. Mỗi dòng policy có thể điền một hoặc cả hai cột (nếu cột nào null thì áp dụng cho tất cả).
* **Ưu điểm**: Cho phép viết các luật kết hợp trực tiếp trên một dòng (ví dụ: `RoleKey = "Member"` AND `MembershipType = "External"` -> `DENY`).
* **Nhược điểm**:
  * **Schema Bloating (Phình to Schema)**: Mỗi khi thêm một thuộc tính phân quyền mới của Member (ví dụ: `Location`, `Department`), ta lại phải ALTER TABLE để thêm cột mới và sửa đổi code kiểm tra.
  * **Code Complexity**: Logic kiểm tra phân quyền trở nên phức tạp do phải handle nhiều trường hợp nullable (`RoleKey` null, `MembershipType` null, hoặc cả hai cùng null).

### 4.2. Đánh giá & Đề xuất Tối ưu nhất (Polymorphic Subject + Deny-Overrides)
Hệ thống WarpTalk quyết định chọn **Phương án A (Polymorphic Subject - sử dụng cặp `SubjectType` và `SubjectKey`)** kết hợp với quy tắc **Deny-Overrides** vì những lý do sau:

1. **Tránh Phình to Schema**: Giữ DB schema là "Source of Truth" tinh gọn, độc lập với các thuộc tính thành viên phát sinh sau này.
2. **Giải quyết triệt để các bài toán kết hợp**: Thay vì lưu tổ hợp AND phức tạp trên một dòng, ta chia thành các dòng đơn giản và để engine xử lý. 
   * *Ví dụ thực tế*: Muốn cho phép `Member` đọc tài liệu nhưng cấm `External Member` đọc tài liệu đó:
     * **Policy 1**: `SubjectType = "Role"`, `SubjectKey = "Member"`, `Effect = "ALLOW"`
     * **Policy 2**: `SubjectType = "MembershipType"`, `SubjectKey = "External"`, `Effect = "DENY"`
     * Do áp dụng **Deny-Overrides**, một người dùng có thuộc tính `Role = Member` và `MembershipType = External` khi check quyền sẽ khớp với cả hai policy trên và kết quả cuối cùng nhận được sẽ là **DENY** (Cấm truy cập).

---

## 5. Proposed Changes

### 5.1. [Workspace Service]

#### [MODIFY] [WorkspaceDocumentAccessPolicy.cs](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Domain/Entities/WorkspaceDocumentAccessPolicy.cs)
* Đổi tên thuộc tính `RoleKey` thành `SubjectKey` để phản ánh đúng bản chất Polymorphic Subject (hoặc tạo Migration đổi tên cột trong Database từ `role_key` thành `subject_key`).

#### [NEW] [DocumentAccessEvaluator.cs](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Services/DocumentAccessEvaluator.cs)
* Cung cấp class Helper/Service chịu trách nhiệm thực thi thuật toán phân quyền:
  * Input: `userId`, `roleName`, `membershipType`, `documentId`, `workspaceId`.
  * Bước 1: Kiểm tra xem tài liệu có phải là tài nguyên thuộc cuộc họp mà user trực tiếp tham gia hay không. Nếu có và trong khoảng thời gian hợp lệ (Grace Period), trả về `ALLOW`.
  * Bước 2: Nạp toàn bộ các chính sách `WorkspaceDocumentAccessPolicy` áp dụng cho `documentId` đó.
  * Bước 3: Lọc ra các chính sách khớp với người dùng:
    * `(SubjectType == "User" && SubjectId == userId)`
    * `(SubjectType == "Role" && SubjectKey == roleName)`
    * `(SubjectType == "MembershipType" && SubjectKey == membershipType)`
  * Bước 4: Áp dụng quy tắc **Deny-Overrides**:
    * Nếu có ít nhất một chính sách khớp có `Effect == "DENY"` -> Trả về `Forbidden`.
    * Nếu không có `DENY` nhưng có ít nhất một chính sách khớp có `Effect == "ALLOW"` -> Trả về `Success`.
    * Nếu không khớp bất kỳ chính sách nào -> Trả về `Forbidden` (Deny-by-default).

---

## 6. Acceptance Criteria (Tiêu chí Nghiệm thu)
1. **API Contract & Integration**: Các API CRUD tài liệu trong Workspace được khai báo chuẩn REST `/api/v1/workspaces/{workspaceId}/documents`. Hỗ trợ các trường hợp lỗi như Permission-denied (403), Missing-file (404), Unsupported-type (400), và Hết hạn lưu trữ (400).
2. **Storage Separation**: Cấu trúc dữ liệu tách biệt rõ ràng Metadata lưu trữ DB và Binary file thực tế (chỉ lưu trữ StorageKey vật lý trong DB, không lưu binary trực tiếp vào DB).
3. **Sensitive Audit logging**: Toàn bộ thao tác Tải lên, Đọc, và Xóa tài liệu nhạy cảm (`is_sensitive = true`) phải tạo bản ghi audit thành công trong bảng `WorkspaceDocumentAudits`.
4. **AI Retrieval boundary**: Kiểm thử chứng minh các tài liệu đã bị xóa mềm (`deleted_at IS NOT NULL`) hoặc chuyển đổi trạng thái lưu trữ (`retention_state = 'archived'`) sẽ bị bộ lọc AI bỏ qua hoàn toàn.

---

## 7. Verification Plan

### Automated Tests
* Tạo bộ unit test cho `DocumentAccessEvaluator` kiểm thử các trường hợp:
  * Trường hợp mặc định (không cấu hình policy): Phải bị từ chối truy cập (Deny-by-default) nếu là tài liệu nhạy cảm.
  * Trường hợp tài liệu thường: Internal Member được đọc mặc định, External Member bị chặn.
  * Trường hợp xung đột chính sách: Role là Member (`ALLOW`) nhưng MembershipType là External (`DENY`) -> Kết quả phải là `DENY` (Deny-overrides).
  * Trường hợp External Member truy cập tài liệu cuộc họp tham gia: Trong thời gian cuộc họp diễn ra -> `ALLOW`; Sau 48 giờ -> `DENY`.
  * Trường hợp cấu hình riêng biệt cho User: User cụ thể được `ALLOW` mặc dù nằm trong nhóm bị `DENY`.
