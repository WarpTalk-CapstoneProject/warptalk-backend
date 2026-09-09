# Spec: Tech Debt — Multi-Language PII Masking & Security Worker Fail-Closed Policy

**Date**: 2026-08-18  
**Status**: proposed  
**Classification**: Tech Debt / Security Enhancement  
**Linear Ticket**: WT-519  

---

## 1. Problem Statement & Context

Trong hệ thống xử lý tài liệu Pre-Ingestion (`DocumentSecurityGuardrailConsumerService` và `security_worker`), các tài liệu chứa dữ liệu nhạy cảm cá nhân (PII) đăng tải trên môi trường Production (ví dụ: `PII_Policy_Violation_Test_Data.docx`) gặp vấn đề không được mask tự động trước khi nạp vào Qdrant Vector DB.

Nội dung nợ kỹ thuật bao gồm:
1. **System Prompt PII chỉ hỗ trợ mẫu Tiếng Anh chuẩn**: Thiếu các định dạng PII Tiếng Việt đặc thù (CCCD 12 chữ số, CMND 9/12 số, SĐT Việt Nam `03x/05x/07x/08x/09x`, Mã số thuế, Họ tên Việt Nam).
2. **Lỗi Fail-Open khi Masking Thất bại**: Khi OpenAI trả về kết quả quét chứa PII nhưng không mask được văn bản, hệ thống Backend vẫn coi văn bản đó là hợp lệ (`canIndex = true`) và lưu văn bản chưa mask vào Vector DB.
3. **Thiếu Lớp Quét Regex Nhanh (Local Fast-Path)** cho các PII cố định trong `security_worker`.

---

## 2. Architecture & Design Solution

```mermaid
sequenceDiagram
    participant Backend as Workspace Service (.NET)
    participant Redis as Redis Streams
    participant Worker as Security Worker (Python)
    participant OpenAI as OpenAI API (gpt-4o-mini)
    participant Qdrant as Qdrant Vector DB

    Backend->>Redis: XADD security:scan_requests (content, pii_enabled=true)
    Redis->>Worker: Consume scan request
    Note over Worker: 1. Run Local Regex Scan (VN Phone, Email, CCCD)
    Worker->>OpenAI: 2. Call OpenAI with Enhanced VN PII System Prompt
    OpenAI-->>Worker: JSON { piiDetected: true, maskedContent: "..." }
    Worker->>Redis: SET security:scan_result:{scanId}
    Backend->>Redis: GET security:scan_result:{scanId}
    
    alt PII Masked Successfully
        Backend->>Qdrant: Publish Embedding Job (Masked Text Only)
    else PII Detected but Unmasked (Fail-Closed)
        Backend->>Backend: Mark IngestionStatus=failed & IngestionFailureReason=PiiUnmasked
        Note over Backend: Block Qdrant Indexing
    end
```

---

## 3. Action Items

- [x] Tạo ticket WT-519 trên Linear gán cho Nhi Ngô (`Todo`).
- [x] Tạo nhánh `fix/wt-519-pii-masking-security-worker` từ `development`.
- [ ] Cập nhật `warptalk-ai/security_worker/scanners.py` bổ sung prompt PII tiếng Việt & quy tắc bảo toàn văn bản.
- [ ] Thêm Local Regex Scanner cho SĐT VN, Email, CCCD 12 số tại Python worker.
- [ ] Cập nhật Backend `DocumentSecurityGuardrailConsumerService.cs` áp dụng quy tắc Fail-Closed khi PII bị rò rỉ hoặc unmasked.
