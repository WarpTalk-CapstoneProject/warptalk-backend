# Feature Specification: Payment Transaction Flow (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-28  
**Status**: Draft  
**Input**: Issue WT-09, Payment Integration Rules & Local Gateway (PayOS)

---

## 1. PayOS Integration Overview

Tài liệu này đặc tả riêng cho luồng giao dịch nạp tiền/thanh toán gói cước tích hợp cổng **PayOS** (hệ thống cung cấp giải pháp nhận chuyển khoản tự động qua dạng VietQR bank transfer).

- Hệ thống WarpTalk sẽ sử dụng Webhook của PayOS làm **nguồn xác nhận duy nhất (Single Source of Truth/SSoT)** cho mọi thanh toán và cấp phát gói cước. 
- Mọi điều hướng Payment Redirect từ Client-side thuần túy chỉ mang tính chất hiển thị UI, **nghiêm cấm** dựa vào Client để đổi trạng thái Order (Theo nguyên tắc ISO Security Review WT-9).

---

## 2. Transaction States (Trạng thái giao dịch)

Mỗi giao dịch (Order/Transaction record) được lưu tại Database gồm các trạng thái State Machine sau:

1. `PENDING`: Transaction được tạo mới. Mã VietQR tạo bằng PayOS checkout-link thành công. Đang chờ khách hàng quét mã.
2. `PROCESSING`: Webhook đã gửi đến, đang trong quá trình thực thi cấp phát Database (Cộng quota) nhằm khóa trạng thái, chống race conditions.
3. `SUCCESS`: Đã nhận webhook của PayOS báo chuyển khoản khớp tiền thành công. Tiền/Quota đã cộng vào Account của người dùng.
4. `FAILED`: Khách hàng hủy giao dịch trên màn hình hiển thị QR hoặc hết hạn Checkout Link.
5. `REFUNDED`: Được quản trị viên (Admin) hoàn tiền/trả lại cho một giao dịch.

---

## 3. Webhook Flow (Luồng xử lý Webhook)

Quá trình luân chuyển dữ liệu thanh toán với cổng PayOS diễn ra như sau:

- **Bước 1 (Initiate)**: Host click Mua Quota/Nâng cấp. 
  - Billing Service tạo bản ghi `Transaction` ở trạng thái `PENDING` (lưu `OrderCode` dạng `int`).
  - Billing Service gọi API PayOS `POST /v2/payment-requests` để lấy Checkout UI Link và trả về cho Web App. Front-end redirect user sang trang của cổng thanh toán.
  
- **Bước 2 (Acknowledge)**: Khách hàng dùng app ngân hàng quyét mã QR và thanh toán xong.
  
- **Bước 3 (Webhook Arrival)**: PayOS bắn Request tới `POST /api/v1/billing/payos/webhook`. Payload sẽ chứa Transaction Info và Signature (chữ ký).
  
- **Bước 4 (Security Verification)**:
  - Hệ thống băm Hash thuật toán HMAC_SHA256 Payload sử dụng chuỗi ký tự bí mật `ChecksumKey` mua qua PayOS.
  - So sánh mã sinh ra và Signature được đính kèm. Khớp thì tiến hành bước sau, sai thì DROP Request, trả 400 Bad Request, cảnh báo Security (Spoofing).

- **Bước 5 (Fulfillment)**:
  - Cập nhật Transaction State: `PENDING` $\rightarrow$ `SUCCESS`.
  - Thực hiện Credit Allocation: Gọi hàm Update gói (`subscription_plan`) hoặc cộng số phút mua thêm (`usage_quota`) tuỳ vào dịch vụ đã đăng ký.

---

## 4. Idempotency (Tính luỹ đẳng)

Nền tảng thanh toán như PayOS có thể bắn Webhook nhiều lần cho cùng một giao dịch (do Timeout network hoặc trigger dư). Endpoint webhook bắt buộc tuân thủ tính Idempotency:

1. Lấy dữ liệu `orderCode` có trong Payload của Webhook.
2. Thực hiện truy vấn kiểm tra Transaction trong database.
3. **Guard Condition (Chốt chặn)**: Nếu Transaction có trạng thái là `SUCCESS` hoặc `PROCESSING`, thì trả thẳng về `200 OK` cho cổng thanh toán mà KHÔNG cấp phát thêm Quota. Tuyệt đối không để xảy ra trường hợp x2 lần Quota cho một hóa đơn thanh toán.
4. Tương tự, nếu Client bấm Re-submit request nhiều lần, Backend cần chặn Duplicate nhờ Idempotency key (tạo mới Order ID mỗi phiên và lưu Tracking).

---

## 5. Retry & Fallback Handling (Xử lý sự cố/Thử lại)

Để vận hành mượt mà với những edge cases lỗi mạng, lỗi database:

1. **Webhook Retries**:
   - Nếu trong quá trình Fulfillment (Bước 5) Database throw Exception (ví dụ Deadlock vì optimistic concurrency), endpoint trả về `500 Internal Server Error`.
   - Cổng PayOS sẽ được thiết kế để tự động thử lại (Retry Webhook) sau X phút. Vì có Idempotency, việc Retry là an toàn.
2. **Polling Job Fallback (Safety Net)**:
   - Trong quá trình mạng đứt nghẽn, Webhook của cổng thanh toán có thể không kết nối được tới Cloud Server của WarpTalk.
   - Xây dựng một Background Service chạy Cronjob: Mỗi 15 phút quét các `Transaction` đang có trạng thái `PENDING` có hạn trên 30 phút. 
   - Chủ động gọi `GET /v2/payment-requests/{orderCode}` thẳng lên server PayOS (Server-to-Server pooling) để kéo cập nhật trạng thái nếu Transaction thực chất phía người dùng đã trả tiền thành công nhưng hệ thống chưa cộng quota do miss Webhook.
