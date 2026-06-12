# Feature Specification: Workspace Document Upload Approval & AI Lifecycle Workflow (WT-158-Addendum)

**Feature Branch**: `feat/workspace-document-approval-workflow`  
**Created/Updated**: 2026-06-05  
**Status**: Proposal  

---

## 1. Problem Statement & Context
Để tăng cường bảo mật thông tin nội bộ của doanh nghiệp (Enterprise), việc tải lên tài liệu mới cần được kiểm soát chặt chẽ thông qua luồng phê duyệt (**Approval Workflow**) và đồng bộ hóa vòng đời tài liệu với hệ thống AI (AI retrieval boundary, Vector DB embedding invalidation).

---

## 2. Quy tắc Nghiệp vụ (Business Rules)

### 2.1. Phân quyền và Trạng thái Tài liệu (Document Status & Ingestion Status Enums)
Tài liệu và quá trình xử lý AI sẽ có các tập trạng thái rõ ràng thay vì dùng chuỗi thô (hardcoded strings):

#### A. Trạng thái Hoạt động của Tài liệu (`WorkspaceDocumentStatus`):
* `active`: Tài liệu hoạt động bình thường, sẵn sàng sử dụng.
* `pending_approval`: Tài liệu do Member tải lên, đang chờ duyệt từ Owner/Admin.
* `rejected`: Đề xuất tài liệu bị từ chối (vẫn lưu lại để Member xem lịch sử, không tự động xóa mềm ngay lập tức).
* `archived`: Tài liệu đã được đưa vào lưu trữ do hết thời hạn lưu giữ (Retention Policy).

#### B. Trạng thái AI Ingestion (`WorkspaceDocumentIngestionStatus`):
* `awaiting_approval`: Tài liệu chưa được duyệt, chưa gửi vào hàng đợi xử lý AI.
* `pending`: Tài liệu đã được duyệt hoặc tải lên trực tiếp bởi Admin/Owner, đang nằm trong hàng đợi chờ xử lý.
* `processing`: Dịch vụ AI đang phân tích dữ liệu, tách nhỏ nội dung (chunking) và sinh embeddings.
* `completed`: AI đã xử lý và lưu vector embeddings thành công vào Vector DB.
* `failed`: Quá trình xử lý AI gặp lỗi (ví dụ: lỗi định dạng, lỗi OCR, hoặc lỗi mạng).

---

### 2.2. Quy trình Tải lên & Phê duyệt (Upload & Approval)
1. **Quyền Upload**:
   - Chỉ các thành viên hợp lệ (`Internal` hoặc `External` được cho phép) mới được upload.
   - Khi **Owner / Admin** upload:
     - `Status = WorkspaceDocumentStatus.active`
     - `IngestionStatus = WorkspaceDocumentIngestionStatus.pending`
     - `AiEligible = true`
     - Gửi ngay sự kiện `PublishDocumentUploadedAsync` lên Redis Stream.
   - Khi **Member** upload:
     - `Status = WorkspaceDocumentStatus.pending_approval`
     - `IngestionStatus = WorkspaceDocumentIngestionStatus.awaiting_approval`
     - `AiEligible = false`
     - **Không** gửi sự kiện lên Redis Stream.
2. **Quy trình duyệt (Approve/Reject)**:
   - **Nơi xử lý**: Được đảm nhận bởi API `POST /api/v1/workspaces/{workspaceId}/documents/{documentId}/approve` và phương thức `WorkspaceDocumentService.ApproveDocumentAsync(...)`.
   - Chỉ **Owner / Admin** mới có quyền duyệt tài liệu.
   - Khi **Approve**:
     - `Status = WorkspaceDocumentStatus.active`
     - `IngestionStatus = WorkspaceDocumentIngestionStatus.pending`
     - `AiEligible = true`
     - Gửi sự kiện `PublishDocumentUploadedAsync` lên Redis Stream để AI Ingestion xử lý.
   - Khi **Reject**:
     - `Status = WorkspaceDocumentStatus.rejected`
     - `AiEligible = false`
     - **Không thực hiện xóa mềm ngay**. Tài liệu vẫn tồn tại trong DB dưới trạng thái `rejected` để Member tải lên có thể biết đề xuất của mình bị từ chối.
3. **Quy trình Xóa tài liệu**:
   - Khi Member hoặc Admin gọi API `DELETE` tài liệu, tài liệu đó mới chính thức bị xóa mềm (`DeletedAt = DateTime.UtcNow`, `DeletedBy = actorId`).

---

### 2.3. Ranh giới AI Retrieval (AI Context Retrieval Boundary)
Hệ thống AI (ví dụ: Chatbot, RAG, Translation Context) **chỉ được phép** lấy dữ liệu từ các tài liệu thỏa mãn đồng thời:
* `DeletedAt == null` (Chưa bị xóa).
* `Status == WorkspaceDocumentStatus.active` (Tài liệu đang hoạt động).
* `RetentionState == "active"` (Chưa bị lưu trữ/archived).
* `IngestionStatus == WorkspaceDocumentIngestionStatus.completed` (AI đã nạp thành công).
* `AiEligible == true` (Đủ điều kiện AI).

Quy tắc này sẽ được cấu hình chặt chẽ trong `DocumentAccessEvaluator` khi kiểm tra quyền truy cập với vai trò `"ai_retrieval"`.

---

### 2.4. Thu hồi Embeddings trong Vector DB (Vector DB Invalidation)
Khi một tài liệu bị **Xóa** (`DeletedAt != null`) hoặc chuyển sang trạng thái **Lưu trữ** (`Status = archived` hoặc `RetentionState = archived`):
1. Hệ thống backend Workspace Service sẽ xuất bản một sự kiện `PublishDocumentDeletedAsync` / `PublishDocumentArchivedAsync` lên Redis Stream.
2. AI Background Worker lắng nghe sự kiện và gọi API của Vector DB để xóa hoàn toàn các vector embeddings liên quan đến `documentId` đó, đảm bảo AI không sử dụng nội dung cũ.
*(Lưu ý: Tài liệu bị Reject từ ban đầu không cần thu hồi embeddings vì chưa bao giờ được nạp vào Vector DB)*.

---

### 2.5. Cơ chế Idempotency & Retry (Idempotency & Resilience)
* **Idempotency**: Để tránh worker xử lý trùng lặp một sự kiện (ví dụ: khi Redis gửi lại tin nhắn do chưa xác nhận thành công), Worker khi nhận event phải kiểm tra `IngestionStatus` trong cơ sở dữ liệu. Nếu trạng thái là `processing` or `completed` thì bỏ qua xử lý.
* **Retry & Dead-Letter (DLQ)**:
  - Khi Ingestion thất bại, Worker được cấu hình tự động thử lại tối đa 3 lần (Exponential Backoff).
  - Nếu sau 3 lần vẫn lỗi, cập nhật `IngestionStatus = WorkspaceDocumentIngestionStatus.failed` để quản trị viên có thể theo dõi và bấm yêu cầu xử lý lại (Retry) thủ công từ giao diện quản trị.

---

### 2.6. Audit Trail (Nhật ký hành động)
Mọi hành động nhạy cảm trên tài liệu phải được ghi lại thành công trong bảng `WorkspaceDocumentAudits`:
* Tải lên tài liệu mật: `UploadDocument`.
* Tải xuống tài liệu mật: `DownloadDocument`.
* Đánh giá phê duyệt tài liệu: `ApproveDocument` hoặc `RejectDocument`.
* Xóa tài liệu: `DeleteDocument`.

---

### 2.7. Kiểm tra quyền ở cả luồng Upload, Download và AI Retrieval
Hệ thống kiểm tra bảo mật ở 3 tầng:
1. **Upload API**: Chặn các vai trò không hợp lệ tải lên.
2. **Download API**: Thêm endpoint `/download` và kiểm tra quyền thông qua `DocumentAccessEvaluator.EvaluateAccessAsync` với permission `"download"`.
3. **AI Retrieval API**: Trước khi hệ thống AI thực hiện tìm kiếm ngữ cảnh, nó phải gửi danh sách Document ID qua bộ đánh giá quyền với permission `"ai_retrieval"`.

---

## 3. API Contract

### 3.1. Phê duyệt/Từ chối tài liệu
* **Endpoint**: `POST /api/v1/workspaces/{workspaceId}/documents/{documentId}/approve`
* **Request Body**:
```json
{
  "approve": true
}
```

### 3.2. Tải xuống tài liệu
* **Endpoint**: `GET /api/v1/workspaces/{workspaceId}/documents/{documentId}/download`
* **Response**: Trả về File Stream (nếu lưu trữ cục bộ) hoặc Redirect đến S3/MinIO Storage Key.
