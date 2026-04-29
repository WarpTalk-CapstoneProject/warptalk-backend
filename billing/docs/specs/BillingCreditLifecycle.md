# Feature Specification: Credit Lifecycle (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-28  
**Status**: Draft  
**Input**: Issue WT-09 & Security/Product requirements  

---

## 1. Credit Lifecycle Overview

Credit Lifecycle định nghĩa toàn bộ quy trình từ lúc hệ thống cấp phát (Allocation) cho đến khi tiêu thụ (Deduction) hoặc hết hạn (Expiration). Trong WarpTalk, đơn vị Credit mặc định được quy đổi thành **Phút AI** (AI Minutes).

Chu trình lifecycle bao gồm 6 giai đoạn chính:

---

## 2. Credit Allocation (Cấp phát)

Cấp phát (Allocation) là quá trình gán mức giới hạn (Quota) cho Workspace/Host.

1. **Subscription Allocation**: 
   - Tự động chạy thông qua Background Cronjob vào lúc `00:00:00 UTC` ở chu kỳ thanh toán (thường là bắt đầu tháng mới).
   - Dựa vào `user_subscription` để cấp lượng AI phút cơ sở (Ví dụ: Free được 30, Pro được 500).
2. **Top-up Allocation (On-demand)**:
   - Các gói cước Pay-as-you-go hoặc tính năng mua thêm tín dụng (Add-on Credits) thông qua VNPay/Stripe webhook.
   - Khi `payment_status = SUCCESS` (từ Stripe/VNPay callback), hệ thống lập tức cộng dồn tín dụng vào `usage_quota`. Top-up credits được phân biệt với Base credits.
3. **Audit Log**:
   - Mọi hoạt động tăng Credit đều được log ghi nhận trong `QuotaAuditLogs` với tham chiếu tới `PlanId` hoặc `TransactionId`.

---

## 3. Credit Deduction (Tiêu hao / Trừ Credit)

Đây là High-Frequency Action (Tác vụ gọi liên tục), cần đảm bảo tính toàn vẹn 100%.

1. **Nguyên tắc "Host-Pays" (Chủ phòng trả phí):**
   - AI Worker tổng hợp lượng âm thanh đã qua xử lý (STT, translation, TTS).
   - Hệ thống bỏ qua Users tham gia (Guests), gửi REST API POST `/api/v1/billing/quota/deduct` kèm `HostId`.
   - Lượng trừ = *Số phút quy đổi từ byte/giây thực tế tiêu thụ*.
2. **Idempotency Key (Chống lặp)**:
   - Worker bắt buộc gửi `idempotencyKey` ở Request Header (Ví dụ: `Deduct_{SessionId}_{Turn_Id}`). 
   - Nếu Timeout Network và Worker retry lại request cũ, Billing Service trả về HTTP 200 (Success) nhưng **không thực thi thao tác trừ trong DB**.
3. **Data Integrity**: 
   - Không xử lý phân tán Read-Modify-Write.
   - Cập nhật phải dựa vào Transaction logic trên Database (Optimistic Concurrency với `RowVersion` ở EF Core) để chặn các cuộc đua (Race Conditions) nếu có $>1$ worker cùng request lúc trừ.

---

## 4. Expiration (Hết hạn)

Tuổi thọ của Credit (Expiration) thay đổi tùy theo xuất xứ của mức hạn ngạch đó.

1. **Subscription Base Quota**:
   - Tất cả giới hạn thuộc về Subscription (VD: 500 phút của Pro) đều có vòng đời **chỉ vọn vẹn trong 1 chu kỳ tính cước** (Thường là 1 tháng).
   - Ở khoảnh khắc hệ thống cấp Allocation cho tháng mới, mọi quota còn lại từ tháng cũ đều bị đánh dấu `IsExpired = true`.
2. **Top-up / Purchased Credit**:
   - Tín dụng mua thêm tự do không bị reset chung hệ thống.
   - Expiration mặc định là **12 tháng (1 năm)** kể từ ngày xuất hóa đơn thành toán thành công, hoặc theo hợp đồng ký riêng nếu là Enterprise.
3. **Thứ tự trừ ưu tiên (Deduction Priority)**: 
   - Base Quota (sắp hết hạn trong tháng) $\rightarrow$ sẽ luôn được trừ trước.
   - Top-up Quota (lâu hết hạn) $\rightarrow$ Trừ sau khi Base Quota cạn.

---

## 5. Rollover (Cộng dồn)

Quy tắc xử lý tín dụng thừa (Rollover) sang chu kỳ tiếp theo:

1. **Free / Pro / Premium Plan**:
   - **Không áp dụng Rollover**. Số phút Base dư khi kết thúc ngày cuối tháng UTC sẽ bị vô hiệu hóa hoàn toàn (Use it or lose it).
2. **Enterprise Plan**:
   - Tùy chỉnh (SLA Contract-based): Doanh nghiệp có thể đàm phán chính sách "Rollover 1 tháng kế tiếp" đối với dung lượng Base Quota không xài hết. 
   - Quota rollover sẽ được gán Type riêng `Rollover_Quota` có HSD duy nhất ở tháng kế tiếp, và tuân theo nguyên tắc Deduct ưu tiên cao nhất trước cả Base Quota tháng mới.

---

## 6. Refund (Hoàn trả)

Cơ chế hoàn trả (Refund / Reverse Deduction) để xử lý bồi thường kỹ thuật:

1. **Sự cố Worker (System Failure):** 
   - Trường hợp AI Worker bị Crash khi đang encode audio nhưng **đã deduct Quota ở đầu quy trình (nếu có pre-deduct)**, Fallback System sẽ fire event báo hủy. Billing Service cần cộng lại số phút đã trừ.
   - Worker nhận dạng chất lượng Audio quá tồi / Silence (nhạc chờ), không ra Output dịch và xác nhận `Effective_Usage = 0`. System gọi API `POST /api/v1/billing/quota/refund`.
2. **Sự cố chất lượng (User Complaint):**
   - API hoàn trả được cấp phép truy cập thông qua RBAC dành cho Customer Support / System Admin để đền bù phút AI cho khách hàng thủ công khi có SLA breach.

---

## 7. Low-Credit Handling (Xử lý khi cạn kiệt)

Sự phối hợp giữa Billing Service, Gateway và Notify Service (Related: WT-38).

1. **Pre-meeting Check (Gateway)**:
   - Request `GET /check` của Gateway block truy cập AI ngay lập tức (Status Code `403`) nếu Tổng Quota của Host bằng `0`.
2. **In-Session Warning Thresholds (Cảnh báo lúc đang họp)**:
   - **Vạch 80% (Warning)**: Khi `usage_quota` tiêu thụ vượt qua $80\%$, Billing Service đẩy Event `Quota_Warning` về Backend Notifications (hoặc WebSocket) báo Client của Chủ phòng (Host): `"Bạn chỉ còn 20% dung lượng AI"`.
   - **Vạch 95% (Critical)**: Tương tự, cảnh báo khẩn cấp (Push Notification đỏ).
3. **Zero / Grace Threshold (Cạn kiệt)**:
   - Khi chạm mốc `0` và meeting vẫn đang Active.
   - Billing đưa Host vào danh sách Exhaused. 
   - Gateway/Worker chủ động ngắt AI processing (Graceful Shutdown) và báo lỗi `Quota_Exceeded_Terminated` thẳng xuống room giao diện hiển thị cho toàn bộ User trong phòng.
