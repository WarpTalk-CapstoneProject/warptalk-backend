# Feature Specification: Data Classification (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 1 Discovery  

---

## 1. Data Classification Overview

Trong quá trình xây dựng Billing & Quota Service, việc xử lý thông tin người dùng và thanh toán đòi hỏi phải phân loại dữ liệu nghiêm ngặt để tuân thủ các quy định bảo mật (như GDPR, PCI-DSS).

Tài liệu này xác định các loại dữ liệu lưu trữ, truyền tải trong hệ thống và mức độ bảo mật tương ứng.

---

## 2. Classification Levels

WarpTalk chia dữ liệu thành 3 cấp độ:

1. **Public/Low Sensitivity (L1)**: Dữ liệu công khai hoặc không nhạy cảm, có thể lộ lọt mà không gây hậu quả lớn.
2. **Internal/Medium Sensitivity (L2)**: Dữ liệu vận hành nội bộ, chỉ nhân viên có thẩm quyền mới được truy cập. Không được public ra ngoài.
3. **Confidential/High Sensitivity (L3 - PII/PCI)**: Dữ liệu nhạy cảm cá nhân (PII) hoặc dữ liệu thanh toán thẻ (PCI). Yêu cầu mã hóa at-rest và in-transit, log obfuscation.

---

## 3. Data Mapping

| Entity / Field | Description | Classification Level | Security Requirement |
|---|---|---|---|
| **Subscription Plan Metadata** | Tên gói cước, số phút base quota, feature flags | L1 - Public | Cache freely, no encryption needed. |
| **Workspace/Host IDs** | UUID của Host và Workspace | L2 - Internal | Che mờ một phần khi log ra ngoài hệ thống. |
| **Usage Quota (Minutes)** | Số phút đã dùng, số phút còn lại | L2 - Internal | Audit log mọi thay đổi. Không nhạy cảm với public nhưng quan trọng về mặt tài chính. |
| **User Name & Email** | Tên và email của người thanh toán | L3 - PII (Confidential) | Mã hóa at-rest, xóa/anonymize khi user xóa account (GDPR RTBF). |
| **Order Code / Transaction ID** | Mã hóa đơn nội bộ và mã giao dịch từ cổng PayOS | L2 - Internal | Bắt buộc index để truy vấn, có thể hiện trên UI cho user. |
| **Payment Amount & Currency** | Số tiền giao dịch (VND/USD) | L2 - Internal | Lưu trữ nguyên bản, không cần mã hóa nhưng cần Immutable logs. |
| **PayOS Webhook Signature** | Chữ ký HMAC để verify webhook | L3 - Confidential | **KHÔNG** log vào application logs. |
| **Credit Card Details (PAN, CVV)** | Số thẻ, mã bảo mật | N/A (Out of Scope) | WarpTalk **không** trực tiếp lưu trữ hay xử lý thẻ. Bắn thẳng sang Stripe/PayOS. (PCI-DSS SAQ A) |
| **Billing Address** | Địa chỉ xuất hóa đơn | L3 - PII (Confidential) | Mã hóa trong DB, che mờ trên UI. |

---

## 4. Compliance & Enforcement Rules

1. **Log Obfuscation (Che mờ Logs)**
   - Mọi HTTP request logger hoặc exception logger **phải loại bỏ** các trường L3 khỏi payload (ví dụ: `email`, `address`, `signature`) trước khi đẩy lên ELK/Datadog.

2. **PCI-DSS Scope Minimization**
   - Không được phép tạo form input nhận số thẻ tín dụng trên UI do backend WarpTalk host.
   - Luôn sử dụng Redirect Checkout (PayOS link) hoặc Hosted Payment Fields (Stripe Elements) để đảm bảo PCI-DSS SAQ A.

3. **Encryption At Rest**
   - Database chứa PII (`BillingAddress`, `PayerEmail`) phải được mã hóa ở mức đĩa (TDE - Transparent Data Encryption) trên PostgreSQL.

4. **Retention Policy**
   - Audit logs (`QuotaAuditLogs`, `TransactionHistory`) giữ tối thiểu 1 năm để giải quyết tranh chấp.
   - PII data sẽ bị xóa bỏ/anonymized sau 30 ngày kể từ khi User yêu cầu xóa tài khoản.
