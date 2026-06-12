# Tài liệu xử lý Logic & User Story - Quản lý tài liệu và AI Retrieval (WT-158)

Tài liệu này giải thích chi tiết cách hệ thống quản lý tài liệu trong WarpTalk xử lý các quy tắc nghiệp vụ (Business Rules), trạng thái lưu trữ (Retention State/Status), và các User Story đã được hiện thực hóa trong mã nguồn.

---

## 1. Mối quan hệ giữa AI Context Retrieval với Status & Retention State

Yêu cầu nghiệp vụ cốt lõi: **"Deleted or archived documents must not be used by AI context retrieval."** (Các tài liệu đã bị xóa hoặc lưu trữ/archived tuyệt đối không được đưa vào làm ngữ cảnh cho AI).

Quy tắc này liên quan trực tiếp đến 3 trường trong thực thể `WorkspaceDocument`:
1. **`DeletedAt` (Xóa mềm - Soft Delete)**:
   - Khi tài liệu bị xóa, `DeletedAt` sẽ được gán giá trị thời gian xóa (khác `null`).
   - Các API lấy danh sách tài liệu (`ListDocumentsAsync`) hoặc kiểm tra quyền truy cập (`EvaluateAccessAsync`) đều lọc điều kiện `DeletedAt == null`.
2. **`RetentionState` (Trạng thái lưu trữ)**:
   - Tài liệu mới tải lên có trạng thái là `"active"` (được định nghĩa qua hằng số `WorkspaceDocumentHelper.RetentionStateActive`).
   - Khi tài liệu hết hạn lưu trữ hoặc được lưu trữ thủ công, trạng thái sẽ chuyển thành `"archived"`.
3. **`AiEligible` (Điều kiện nạp AI)**:
   - Thuộc tính boolean để xác định tài liệu có đủ điều kiện nạp làm dữ liệu cho AI hay không. 
   - Khi tài liệu bị xóa (`DeletedAt != null`) hoặc bị đưa vào trạng thái lưu trữ (`RetentionState == "archived"`), cờ `AiEligible` bắt buộc phải chuyển sang `false` để các truy vấn RAG (Retrieval-Augmented Generation) của AI bỏ qua hoàn toàn.

> [!IMPORTANT]
> Cơ sở dữ liệu đã cấu hình sẵn Index tối ưu: `entity.HasIndex(e => new { e.WorkspaceId, e.AiEligible }, "idx_workspace_documents_workspace_ai")` để phục vụ riêng cho việc lọc nhanh các tài liệu hợp lệ cung cấp ngữ cảnh cho AI.

---

## 2. Các User Story & Luồng Logic Đã Hiện Thực Trong Code

Dưới đây là các câu chuyện người dùng (User Stories) và cách chúng được giải quyết trong dịch vụ [WorkspaceDocumentService.cs](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Services/WorkspaceDocumentService.cs):

### Story 1: Tải lên tài liệu và kích hoạt AI Ingestion (Upload Document)
* **User Story**: *Là một thành viên Workspace, tôi muốn tải lên tài liệu mới để hệ thống lưu trữ và chuẩn bị dữ liệu cho AI xử lý.*
* **Logic xử lý**:
  - Xác thực người tải lên phải là thành viên hợp lệ và Workspace đang hoạt động.
  - Khởi tạo metadata tài liệu với các trạng thái mặc định:
    - `AiEligible = true` (Cho phép AI học).
    - `IngestionStatus = "pending"` (Đang chờ xử lý).
    - `Status = "active"`, `RetentionState = "active"`.
  - Đẩy sự kiện `PublishDocumentUploadedAsync` vào Redis Stream để dịch vụ AI chạy ngầm thực hiện trích xuất nội dung văn bản (RAG ingestion pipeline).

### Story 2: Bảo vệ tài liệu trong lúc AI xử lý (Ingestion Security boundary)
* **User Story**: *Tôi muốn tài liệu vừa tải lên được bảo mật, chỉ tôi và Admin xem được cho đến khi hệ thống phân loại mức độ nhạy cảm xong.*
* **Logic xử lý**:
  - Tại bộ đánh giá quyền [DocumentAccessEvaluator.cs](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Evaluators/DocumentAccessEvaluator.cs), khi `IngestionStatus == "pending"`:
    - Chỉ cho phép **Workspace Owner/Admin** hoặc người sở hữu tài liệu (**Document Owner**) truy cập.
    - Trả về mã lỗi `AccessDeniedPendingIngestion` cho các đối tượng khác.

### Story 3: Phân quyền truy cập dựa trên chính sách (Deny-Overrides Policy)
* **User Story**: *Tôi muốn thiết lập các quy định cụ thể, ví dụ: cho phép vai trò Member đọc, nhưng cấm External Member đọc tài liệu mật.*
* **Logic xử lý**:
  - Engine phân quyền nạp toàn bộ các chính sách từ bảng `WorkspaceDocumentAccessPolicies`.
  - Áp dụng nguyên tắc **Cấm ghi đè cho phép (Deny-Overrides)**:
    - Nếu khớp bất kỳ chính sách `DENY` nào $\rightarrow$ Từ chối ngay lập tức (`AccessDeniedByPolicy`).
    - Nếu không có `DENY` nhưng có ít nhất một chính sách `ALLOW` $\rightarrow$ Cho phép truy cập.

### Story 4: Quyền mặc định & Phân vùng Đối tác ngoài (External Member Grace Period)
* **User Story**: *Tôi muốn đối tác ngoài chỉ được đọc tài liệu liên quan đến cuộc họp họ tham gia trong thời gian diễn ra và trong vòng 24 giờ sau khi kết thúc cuộc họp.*
* **Logic xử lý**:
  - **Tài liệu nhạy cảm (`IsSensitive = true`)**: Mặc định từ chối truy cập (Deny-by-default) trừ khi có chính sách gán cụ thể.
  - **Tài liệu thường (`IsSensitive = false`)**:
    - `Internal Member`: Mặc định cho phép truy cập.
    - `External Member`: Bị từ chối mặc định.
    - **Ngoại lệ cuộc họp**: Nếu tài liệu sinh ra từ một cuộc họp (`SourceType == "meeting"`):
      1. Kiểm tra External Member có nằm trong danh sách người tham gia cuộc họp (`participants`) hay không thông qua `ITranslationRoomClient`.
      2. Đo thời gian kể từ lúc cuộc họp kết thúc so với thời gian Grace Period cấu hình (mặc định 24 giờ, hoặc cấu hình đè bằng trường `ExternalGracePeriodHours` trong settings của Workspace). Nếu nằm trong khoảng này $\rightarrow$ Cho phép truy cập tạm thời.

### Story 5: Quản trị chính sách tài liệu (Manage Access Policy)
* **User Story**: *Tôi muốn thêm hoặc xóa các chính sách phân quyền đối với tài liệu do tôi sở hữu hoặc do tôi quản lý.*
* **Logic xử lý**:
  - Trước khi thêm (`Add`) hoặc xóa (`Remove`) một chính sách, hệ thống gọi `CanManagePoliciesAsync`.
  - Quyền quản trị chỉ được cấp cho **Workspace Owner**, **Workspace Admin**, hoặc chính chủ sở hữu tài liệu đó.
