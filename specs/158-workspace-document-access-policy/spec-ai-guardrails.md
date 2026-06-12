# Feature Specification: Workspace Document AI Integration & Security Guardrails (WT-159)

**Feature Branch**: `feat/workspace-document-ai-guardrails`  
**Created**: 2026-06-05  
**Updated**: 2026-06-08 (Aligned with WT-158 Access Policy and Code Logic)  
**Status**: Proposed  
**Input**: Linear ticket WT-159 - Workspace Document AI Integration, Hybrid Glossary Retrieval, and Security Guardrails (DLP, PII Redaction/Masking)

---

## 1. Problem Statement

Khi hệ thống WarpTalk mở rộng tích hợp tính năng Tìm kiếm Ngữ nghĩa (Semantic Search/RAG) dựa trên tài liệu doanh nghiệp sử dụng **Qdrant Vector Database**, một số thách thức lớn về an toàn thông tin và hiệu năng xuất hiện:
1. **Lạm dụng dữ liệu cá nhân & rò rỉ thông tin nhạy cảm:** Gửi trực tiếp tài liệu chưa qua xử lý lên các mô hình LLM công cộng (OpenAI, Gemini) có nguy cơ vi phạm bảo mật dữ liệu khách hàng (PII) hoặc làm rò rỉ dữ liệu tài chính nhạy cảm của doanh nghiệp (ví dụ: con số doanh thu thực tế, bí mật kinh doanh).
2. **Kiểm soát quyền truy cập tài liệu:** Cần đảm bảo hệ thống không sử dụng tài liệu đã bị xóa (Soft Delete) hoặc tài liệu đã đưa vào lưu trữ (Archived) làm ngữ cảnh cho AI. Đồng thời phân quyền truy cập tài liệu (Access Policy) cực kỳ khắt khe theo đúng thiết kế của WT-158.
3. **Quản lý Glossary (Thuật ngữ chuyên ngành) quy mô lớn:** Khi doanh nghiệp sở hữu danh mục hàng ngàn thuật ngữ chuyên ngành, việc nhồi nhét tất cả vào Prompt dịch của LLM gây tràn Context Window (giới hạn Token) và làm tăng chi phí API.

Để giải quyết bài toán này, WarpTalk cần triển khai kiến trúc tích hợp AI có phân vùng dữ liệu rõ ràng, kết hợp **PostgreSQL** (Source of Truth quan hệ) & **Qdrant** (Vector Store), cùng các bộ rào chắn an toàn (**AI Guardrails**) linh hoạt được cấu hình thông qua trường `AiUsagePolicy` của từng tài liệu hoặc kế thừa từ Workspace.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Phân định vai trò PostgreSQL vs Qdrant và Quản lý Trạng thái Vòng đời (WT-158 Alignment)
Hệ thống duy trì cơ sở dữ liệu PostgreSQL làm **Source of Truth** duy nhất và Qdrant làm **Vector Store** phục vụ tìm kiếm ngữ nghĩa (Semantic Search).

#### A. Trạng thái Hoạt động của Tài liệu (`WorkspaceDocumentStatus`):
* `active`: Tài liệu hoạt động bình thường, sẵn sàng sử dụng.
* `pending_approval`: Tài liệu do Member tải lên, đang chờ duyệt từ Owner/Admin.
* `rejected`: Đề xuất tài liệu bị từ chối (vẫn lưu lại để Member xem lịch sử, không tự động xóa mềm ngay lập tức).
* `archived`: Tài liệu đã được đưa vào lưu trữ (chuyển sang trạng thái lưu trữ thủ công hoặc tự động do hết hạn lưu giữ).

#### B. Trạng thái AI Ingestion (`WorkspaceDocumentIngestionStatus`):
* `awaiting_approval`: Tài liệu chưa được duyệt, chưa gửi vào hàng đợi xử lý AI (chỉ áp dụng khi Member upload).
* `pending`: Tài liệu đã được duyệt hoặc tải lên trực tiếp bởi Admin/Owner, đang nằm trong hàng đợi chờ xử lý.
* `processing`: Dịch vụ AI đang phân tích dữ liệu, quét DLP/PII, tách nhỏ nội dung (chunking) và sinh embeddings.
* `completed`: AI đã xử lý, quét an toàn và lưu vector embeddings thành công vào Qdrant Vector DB.
* `failed`: Quá trình xử lý AI gặp lỗi (ví dụ: lỗi định dạng, lỗi OCR, hoặc lỗi hệ thống).

#### C. Quy trình Tải lên & Phê duyệt (WT-158 Logic):
1. **Khi Owner / Admin upload**:
   * `Status = active`
   * `IngestionStatus = pending`
   * `AiEligible = true`
   * Tự động gửi sự kiện `DocumentUploaded` lên Redis Stream `workspace-document-events` để background consumer xử lý.
2. **Khi Member upload**:
   * `Status = pending_approval`
   * `IngestionStatus = awaiting_approval`
   * `AiEligible = false`
   * **Không** gửi sự kiện lên Redis Stream cho đến khi được duyệt.
3. **Quy trình duyệt (Approve/Reject)** bởi Owner / Admin:
   * **Approve**: 
     * `Status = active`
     * `IngestionStatus = pending`
     * `AiEligible = true`
     * Gửi sự kiện `DocumentUploaded` lên Redis Stream `workspace-document-events` để thực hiện RAG Ingestion.
   * **Reject**:
     * `Status = rejected`
     * `AiEligible = false`
     * Tài liệu không được gửi đi xử lý AI, không sinh embeddings.
4. **Quy trình Xóa / Lưu trữ**:
   * Khi tài liệu bị xóa mềm (`DeletedAt != null`) hoặc được chuyển trạng thái lưu trữ (`RetentionState == "archived"` hoặc `Status == archived`):
     * Cờ `AiEligible` bắt buộc phải chuyển sang `false` để ngăn RAG query truy vấn.
     * Hệ thống gửi sự kiện `DocumentDeleted` lên Redis Stream để Background Worker thực hiện thu hồi/xóa hoàn toàn các vector embeddings liên quan khỏi Qdrant Vector DB.

---

### 2.2. Cấu trúc AI Usage Policy (`AiUsagePolicy` JSON)
Bổ sung cấu hình chính sách AI linh hoạt cho từng tài liệu thông qua trường `AiUsagePolicy` (lưu trữ dưới dạng JSON string) tại PostgreSQL:

* **Hành vi kế thừa (Hierarchy & Fallback):**
  Hệ thống áp dụng chính sách theo thứ tự ưu tiên giảm dần:
  $$\text{Effective Policy} = \text{Document-level Policy} \text{ (nếu cấu hình)} \rightarrow \text{Workspace-level Settings} \text{ (nếu cấu hình)} \rightarrow \text{Default Fallback}$$
  Nếu tài liệu có cấu hình riêng ở trường `AiUsagePolicy`, hệ thống áp dụng cấu hình đó. Nếu giá trị là `null` hoặc không cấu hình thuộc tính, hệ thống tự động kế thừa cấu hình mặc định tại `WorkspaceConfiguration.AiUsagePolicy` của Workspace.

* **Cấu trúc dữ liệu cấu hình (`AiUsagePolicyConfiguration`):**
  ```json
  {
    "allow_external_llm": true,
    "redact_pii": {
      "enabled": true
    },
    "dlp": {
      "enabled": true,
      "keywords_blacklist": ["doanh thu", "bí mật kinh doanh"]
    },
    "translation_profile": {
      "translation_tone": "formal",
      "language_specific_rules": {
        "vietnamese_honorific_style": "kính gửi",
        "japanese_honorific_style": "desu/masu"
      }
    }
  }
  ```

---

### 2.3. Rào chắn bảo vệ Ingestion & Quyền truy cập AI Retrieval (WT-158 Evaluator Alignment)
Hệ thống tích hợp các logic phân quyền kiểm tra bảo mật ở bộ đánh giá quyền truy cập `DocumentAccessEvaluator`:

1. **Rào chắn bảo vệ trong lúc AI xử lý (Ingestion Security boundary)**:
   * Khi tài liệu có trạng thái `IngestionStatus == "pending"` hoặc `IngestionStatus == "awaiting_approval"`:
     * Chỉ cho phép **Workspace Owner/Admin** hoặc người sở hữu tài liệu (**Document Owner / UploadedBy**) truy cập xem/đọc hoặc tải tài liệu.
     * Mọi thành viên khác (kể cả Internal/External Member thông thường) đều bị chặn và trả về mã lỗi `AccessDeniedPendingIngestion` (403 Forbidden).
2. **Quyền truy cập AI Retrieval (AI Context Retrieval Boundary)**:
   * Bộ đánh giá quyền khi kiểm tra với permission `"ai_retrieval"` bắt buộc phải thực thi bộ lọc cứng. Một tài liệu chỉ được cung cấp làm ngữ cảnh RAG cho AI khi thỏa mãn đồng thời:
     * `DeletedAt == null` (Chưa bị xóa mềm).
     * `Status == active` (Tài liệu đang hoạt động).
     * `RetentionState == "active"` (Chưa bị lưu trữ/archived).
     * `IngestionStatus == "completed"` (AI đã nạp thành công).
     * `AiEligible == true` (Đủ điều kiện AI).

---

### 2.4. Công nghệ triển khai Guardrails (PII & DLP Scanning)
Tiến trình chạy nền `DocumentAiIngestionConsumerService` chịu trách nhiệm lắng nghe sự kiện tải lên và thực thi các bộ rào chắn an toàn trước khi nạp tài liệu:

1. **PII Redaction & Masking:**
   * Quét và phát hiện các thông tin định danh cá nhân nhạy cảm trong tài liệu dựa trên Regular Expressions hoặc các thư viện chuyên dụng (như Microsoft Presidio).
   * Các pattern bắt buộc nhận diện:
     * `Email`: `[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}`
     * `Phone Number` (định dạng Việt Nam): `\b(?:\+?84|0)\d{9,10}\b`
2. **DLP (Data Loss Prevention):**
   * Quét và phát hiện sự tồn tại của các từ khóa nhạy cảm nằm trong `keywords_blacklist` được cấu hình từ chính sách hiệu dụng (Effective Policy).
3. **Logic Cập nhật Trạng thái sau khi quét:**
   * Nếu phát hiện bất kỳ vi phạm PII hoặc DLP nào:
     * Tự động đánh dấu `IsSensitive = true` (hoặc giữ nguyên nếu đã là `true` từ trước).
     * Cập nhật `ConfidentialityLevel = "restricted"` (thông qua `WorkspaceDocumentHelper.GetConfidentialityLevel`).
     * Tự động đặt cờ `AiEligible = false` (Không cho phép nạp vector làm ngữ cảnh AI để tránh rò rỉ thông tin ra các LLM bên ngoài).
   * Nếu không phát hiện vi phạm:
     * Giữ nguyên thuộc tính `IsSensitive` ban đầu.
     * Cập nhật `ConfidentialityLevel` tương ứng.
     * Đặt cờ `AiEligible = !IsSensitive`.
   * Cập nhật `IngestionStatus = "completed"`.
4. **Cơ chế Phòng ngừa Sự cố (Fail-Safe Fallback):**
   * Trong trường hợp tiến trình quét AI Ingestion gặp lỗi ngoại lệ (Exception), hệ thống áp dụng cơ chế bảo mật nghiêm ngặt nhất (Fail-Safe) để ngăn chặn rò rỉ thông tin:
     * Đặt `IsSensitive = true`
     * Đặt `ConfidentialityLevel = "restricted"`
     * Đặt `AiEligible = false`
     * Đặt `IngestionStatus = "failed"`

---

### 2.5. Cơ chế Glossary lai (Hybrid Glossary Architecture)
* **Khớp chính xác (Exact Match B-Tree):** Các thuật ngữ ngắn, chính xác tuyệt đối được tìm kiếm trực tiếp trên bảng `transcript.glossary_terms` trong PostgreSQL sử dụng B-Tree Index để tìm kiếm cực nhanh trong thời gian thực.
* **Khớp ngữ nghĩa (Semantic Glossary Retrieval):** Đối với các tệp thuật ngữ lớn, hệ thống sẽ đồng bộ chúng vào Qdrant dưới dạng vector. Trước khi dịch một đoạn văn bản:
  1. Dùng đoạn văn bản gốc truy vấn ngữ nghĩa trên Qdrant để lọc ra top **5-10 thuật ngữ** liên quan nhất.
  2. Chỉ chèn 5-10 thuật ngữ này vào System Prompt gửi lên LLM để tối ưu chi phí token và tốc độ phản hồi.

---

## 3. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - Ingestion Pipeline & Access Security (WT-158 & WT-159 combined)
*Là thành viên workspace, tôi muốn tài liệu mới upload được bảo mật và không bị rò rỉ ra ngoài trong quá trình AI đang phân tích.*
1. **Given** User X là Member thông thường tải lên một tài liệu mới,  
   **When** Hệ thống lưu tài liệu vào PostgreSQL với trạng thái `Status = "pending_approval"` và `IngestionStatus = "awaiting_approval"`,  
   **Then** Chỉ có Workspace Owner, Admin hoặc User X (Document Owner) mới có quyền truy cập đọc/xem tài liệu này. Các Member khác nhận lỗi `AccessDeniedPendingIngestion` (403).
2. **Given** Tài liệu của Member đang ở trạng thái `awaiting_approval`,  
   **When** Workspace Admin phê duyệt tài liệu này,  
   **Then** Trạng thái cập nhật thành `Status = "active"` và `IngestionStatus = "pending"`, đồng thời hệ thống gửi sự kiện `DocumentUploaded` vào Redis Stream.
3. **Given** Tài liệu ở trạng thái `IngestionStatus = "pending"`,  
   **When** Background Worker thực thi và gặp lỗi không xác định trong quá trình phân tách tài liệu,  
   **Then** Hệ thống áp dụng Fail-Safe: Cập nhật `Status` thành `active`, `IngestionStatus` thành `failed`, `IsSensitive` thành `true`, và `AiEligible` thành `false` để đảm bảo an toàn.

### User Story 2 - AI Usage Policy Enforcement (DLP & PII Masking)
*Là quản trị viên hệ thống, tôi muốn đảm bảo các tài liệu nhạy cảm được tự động che giấu thông tin cá nhân và thông tin doanh số trước khi gửi lên các API AI bên ngoài.*
1. **Given** Tài liệu D có cấu hình hiệu dụng `RedactPii.Enabled = true`,  
   **When** Tiến trình background scan quét nội dung chứa email `john.doe@example.com` và số điện thoại `0987654321`,  
   **Then** Hệ thống phát hiện vi phạm PII, tự động cập nhật cờ `IsSensitive = true` và `AiEligible = false`, ngăn không cho tài liệu này được nạp vào Vector DB.
2. **Given** Tài liệu D có cấu hình hiệu dụng `Dlp.Enabled = true` với `KeywordsBlacklist = ["doanh thu"]`,  
   **When** Tiến trình background scan phát hiện nội dung chứa từ khóa "doanh thu",  
   **Then** Hệ thống ghi nhận vi phạm DLP, tự động cập nhật cờ `IsSensitive = true` và `AiEligible = false`.

### User Story 3 - Hybrid Glossary Matching
*Là hệ thống dịch thuật, tôi muốn tìm kiếm chính xác thuật ngữ ngắn qua SQL và lọc thông minh thuật ngữ dài qua Qdrant để tối ưu hóa Prompt.*
1. **Given** Glossary chứa từ khóa "WarpTalk" $\rightarrow$ "WarpTalk App",  
   **When** Người dùng nói câu chứa từ khóa "WarpTalk",  
   **Then** Hệ thống truy vấn PostgreSQL và dịch chính xác từ khóa này.
2. **Given** Một tài liệu Glossary lớn chứa 3,000 thuật ngữ,  
   **When** Hệ thống chuẩn bị dịch đoạn hội thoại về kỹ thuật truyền dẫn,  
   **Then** Hệ thống quét Qdrant để tìm 10 thuật ngữ tương đồng nhất về mặt ngữ nghĩa và chèn chúng vào context dịch của LLM.

---

## 4. Requirements

* **FR-159-001:** Hệ thống MUST duy trì PostgreSQL làm Source of Truth để quản lý metadata, trạng thái vòng đời tài liệu (`WorkspaceDocumentStatus`, `WorkspaceDocumentIngestionStatus`) và quyền truy cập (ACL).
* **FR-159-002:** Hệ thống MUST đồng bộ trạng thái xóa mềm (`DeletedAt != null`) hoặc lưu trữ (`RetentionState == "archived"`) để tự động gán `AiEligible = false`, loại bỏ tài liệu khỏi ngữ cảnh tìm kiếm của Qdrant bằng cách xuất bản sự kiện xóa/lưu trữ lên Redis Stream.
* **FR-159-003:** Hệ thống MUST hỗ trợ cấu hình chính sách AI (`AiUsagePolicy` JSON string) ở cả cấp độ ghi đè tài liệu (Document Override) và mặc định workspace (Workspace Default - `WorkspaceConfiguration`).
* **FR-159-004:** Hệ thống MUST hỗ trợ tính năng **PII Redaction/Masking** trong background service để tự động phát hiện email, số điện thoại và cập nhật cờ `IsSensitive = true` và `AiEligible = false`.
* **FR-159-005:** Hệ thống MUST hỗ trợ bộ lọc **DLP (Data Loss Prevention)** trong background service để phát hiện các từ khóa nhạy cảm trong `KeywordsBlacklist` và tự động cập nhật cờ `IsSensitive = true`, `AiEligible = false`.
* **FR-159-006:** Hệ thống MUST hỗ trợ cơ chế dịch thuật lai: Khớp chính xác qua SQL B-Tree Index và khớp ngữ nghĩa lọc thuật ngữ qua Qdrant Vector Search.

---

## 5. Security & Regression Risks

* **Trễ đồng bộ Vector DB (Eventual Consistency Risk):** Độ trễ cập nhật giữa PostgreSQL và Qdrant có thể khiến tài liệu vừa bị xóa/vừa bị thay đổi chính sách bảo mật vẫn tạm thời xuất hiện trong kết quả tìm kiếm của AI. **Biện pháp giảm thiểu:** Cần áp dụng bộ lọc cứng (Hard filter) theo `document_id` hợp lệ được lấy từ kết quả truy vấn SQL có kiểm tra quyền truy cập (`DocumentAccessEvaluator` với permission `"ai_retrieval"`) trước khi gửi yêu cầu tìm kiếm vector sang Qdrant.
* **Bypass bộ lọc Guardrails:** Người dùng có thể cố tình sử dụng các kỹ thuật "jailbreak prompt" để ép LLM dịch hoặc tiết lộ thông tin nhạy cảm đã bị che mờ. **Biện pháp giảm thiểu:** Cần thực hiện kiểm duyệt và lọc cả đầu ra (Output Moderation) sau khi LLM trả về kết quả dịch.
* **Tràn Ingestion Task Queue:** Khi tải lên lượng lớn tài liệu đồng thời, hàng đợi Redis Stream có thể bị nghẽn làm trễ quá trình Ingestion. **Biện pháp giảm thiểu:** Thiết lập cơ chế phân phối tải (Load Balancing) cho background consumer và giới hạn kích thước file tải lên của Workspace.

---

## 6. Verification Plan

### Automated Tests
* Tạo bộ unit test và integration test bổ sung cho `DocumentAccessEvaluator` và `DocumentAiIngestionConsumerService`:
  * **Test Ingestion Security:** Đảm bảo khi `IngestionStatus` là `pending` hoặc `awaiting_approval`, chỉ Owner/Admin/Uploader mới có quyền truy cập, các member khác bị cấm và nhận lỗi `AccessDeniedPendingIngestion`.
  * **Test AI Retrieval Access Boundary:** Đảm bảo chỉ tài liệu có `Status == active`, `RetentionState == active`, `IngestionStatus == completed` và `AiEligible == true` mới được đánh giá hợp lệ cho `ai_retrieval`.
  * **Test Ingestion Consumer Scan (PII & DLP):**
    * Khi tài liệu vi phạm PII (email/số điện thoại) hoặc DLP (từ khóa đen): Kiểm tra cờ `IsSensitive` được set thành `true` và `AiEligible` được set thành `false`.
    * Khi tài liệu sạch: Kiểm tra cờ `IsSensitive` giữ nguyên và `AiEligible` được set thành `!IsSensitive`.
    * Khi Ingestion gặp lỗi ngoại lệ: Kiểm tra cơ chế Fail-Safe tự động cập nhật `IsSensitive = true`, `AiEligible = false` và `IngestionStatus = failed`.
  * **Test Policy Inheritance & Fallback:** Đảm bảo thuộc tính của tài liệu kế thừa cấu hình từ Workspace nếu tài liệu không ghi đè chính sách riêng.

### Manual Verification
* Deploy và chạy thử nghiệm background job, tải lên tài liệu chứa thông tin nhạy cảm kiểm tra log xem có quét đúng và đánh dấu `AiEligible = false` hay không.
* Thực hiện gửi yêu cầu dịch thuật trong Translation Room để kiểm tra tính năng lọc Glossary lai hoạt động đúng hiệu năng.
