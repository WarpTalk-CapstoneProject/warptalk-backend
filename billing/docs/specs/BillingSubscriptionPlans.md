# Feature Specification: Subscription Plans (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-28  
**Status**: Draft  
**Input**: Issue WT-09 & Security/Product requirements  

---

## 1. Subscription Models

WarpTalk áp dụng mô hình Tiered Subscriptions để phân cấp quyền truy cập tính năng (Feature Access) và dung lượng tài nguyên (Credit Allocation). Dưới đây là thông số kỹ thuật cho 4 gói cước chính.

### 1.1. Free Plan
- **Mục tiêu**: Để người dùng trải nghiệm chất lượng cơ bản của hệ thống WarpTalk trước khi quyết định nâng cấp.
- **Credit Allocation**: Cấp 30 phút / tháng (Tự động reset vào ngày 1 hàng tháng theo UTC).
- **Feature Access Rules**:
  - **Models**: Standard STT, Standard Translation.
  - **Latency**: Shared infrastructure (Normal Priority).
  - **Voices**: Standard TTS Voices (Giới hạn ngôn ngữ / giọng đọc).
  - **Meeting Size**: Tối đa 5 người tham gia trong một cuộc họp.
  - **Limits**: Không được truy cập các tính năng nâng cao như Export Transcript, Auto Summary.

### 1.2. Pro Plan
- **Mục tiêu**: Dành cho người dùng cá nhân (freelancers, educators) cần tổ chức cuộc họp cường độ trung bình với chất lượng cao.
- **Credit Allocation**: Cấp 500 phút / tháng (không hỗ trợ cộng dồn sang tháng sau).
- **Feature Access Rules**:
  - **Models**: Advanced Translation Models (độ chính xác cao hơn, ít delay).
  - **Latency**: Priority Queue (ưu tiên xử lý so với Free).
  - **Voices**: Premium TTS Voices (Nhiều lựa chọn ngữ điệu tự nhiên, đa ngôn ngữ), Hỗ trợ Clone Voice.
  - **Meeting Size**: Tối đa 25 người tham gia.
  - **Add-ons**: Cho phép tải xuống (Export) raw Transcript sau cuộc họp.

### 1.3. Premium Plan
- **Mục tiêu**: Dành cho các nhóm làm việc (teams, SMBs) có nhu cầu họp liên tục, cần tích hợp AI meeting summary và đa ngôn ngữ chuyên sâu.
- **Credit Allocation**: Cấp 1,000 phút / tháng. Hỗ trợ mua thêm (Top-up credits) theo dạng pay-as-you-go khi hết.
- **Feature Access Rules**:
  - **Models**: State-of-the-art Translation, hỗ trợ thuật ngữ chuyên biệt (Custom Dictionaries).
  - **Latency**: Zero-latency Streaming (Dedicated compute priority).
  - **Voices**: Voice Cloning / Private Voices.
  - **Meeting Size**: Tối đa 100 người tham gia.
  - **Features**: Tự động tạo Meeting Summary & Action Items bằng AI, API access nội bộ.

### 1.4. Enterprise Plan
- **Mục tiêu**: Khách hàng doanh nghiệp lớn với nhu cầu tùy chỉnh, bảo mật nội bộ và SLAs rõ ràng.
- **Credit Allocation**: Custom Volume (Bắt đầu từ 10,000 phút/tháng) hoặc tính phí thực tế cuối kỳ (Invoice billing).
- **Feature Access Rules**:
  - Dành riêng GPU instances (Isolating compute nodes).
  - Tuân thủ HIPAA / SOC2 cho lưu trữ dữ liệu hội thoại nội bộ doanh nghiệp.
  - Hỗ trợ SAML/SSO Authentication và RBAC nhiều cấp.
  - Không giới hạn quy mô meeting, hỗ trợ live broadcasting (Webinar streaming).

---

## 2. Credit Lifecycle & Allocation Rules

### Allocation Timeline
- **Chu kỳ**: Hệ thống cấp mới (renew) hoặc hoàn trả vạch mặc định (reset) vào `00:00:00 UTC` của ngày đầu tiên trong chu kỳ tính phí.
- Nếu người dung đang ở Free Plan, hệ thống cấp 30 phút. 
- Nếu người dùng thay đổi Plan, Credit sẽ được điều chỉnh (Prorated) theo chính sách Upgrade/Downgrade (xử lý ở Phase 2).

### Consumption Tracking
- **Quy tắc trừ**: Tiêu hao (consumption) dựa trên thời gian thực tế sử dụng AI. Chỉ trừ vào quota của **Host** (Chủ phòng).
- **Idempotency**: Requests trừ điểm phải kèm Idempotency Key (VD: `meetingId_seqNumber`) để đảm bảo không deduct 2 lần ngay cả khi AI Worker timeout hoặc thử lại.
- **Double Enforcement**: 
  - Gateway pre-check (trước meeting): Nếu quota <= 0 -> Không cho phép khởi động AI Bot.
  - Worker check (trong khi xử lý): Cập nhật dần và nếu phát hiện cạn kiệt, trigger event "Quota Exhausted" cho Client. 

### Overage constraints (Quá hạn mức)
- **Hard Limit**: Đối với Free và Pro Plan, khi tiêu thụ đến 0, hệ thống thực thi ngắt kết nối voice AI. (Có thể có Grace Period threshold -1 phút để phòng họp không ngắt quá abrupt).
- **Soft Limit**: Đối với Premium Plan, khi chạm ngưỡng 0, hệ thống chuyển sang chế độ "Pay-as-you-go" để trừ chi phí vào cuối tháng, Host nhận được Notify cảnh báo (WT-38).

---

## 3. Implementation Notes

Thiết kế Database (dựa theo commit `fa8b7c7`) bao gồm:
- Bảng `subscription_plan`: Định nghĩa metadata của 4 gói (Tên, Limits cơ sở).
- Bảng `usage_quota`: Ghi nhận Allocate amount (Ví dụ: 30, 500, 1000) và Used amount (tiêu thụ hàng ngày).
- Endpoint `/api/v1/billing/quota/check` sẽ filter theo ID và lấy Plan settings tương ứng để check Feature Access (ví dụ: host request *Premium Voice* nhưng đang ở *Free Plan* -> Trả về `403 Forbidden`).
