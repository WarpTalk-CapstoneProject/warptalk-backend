# Feature Specification: Database Schema Contract (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 1 Discovery  

---

## 1. Schema Overview

Billing & Quota Service sử dụng cơ sở dữ liệu quan hệ (PostgreSQL) để đảm bảo tính toàn vẹn dữ liệu (ACID) cho các giao dịch tài chính và hạn ngạch. Các bảng chính bao gồm:
- `SubscriptionPlans`: Cấu hình các gói cước.
- `UsageQuotas`: Quản lý số phút hiện tại của từng Host/Workspace.
- `Transactions`: Ghi nhận lịch sử giao dịch nạp tiền.
- `QuotaAuditLogs`: Theo dõi lịch sử biến động quota (Cộng/Trừ) để đối soát.

---

## 2. Table Definitions

### 2.1. Bảng `SubscriptionPlans` (Danh mục gói cước)

Bảng này chứa dữ liệu cấu hình tĩnh cho các gói dịch vụ (Free, Pro, Premium, v.v.). Thường ít bị thay đổi.

| Column Name | Data Type | Constraint | Description |
|---|---|---|---|
| `Id` | `UUID` | PK | Mã định danh gói cước |
| `Name` | `VARCHAR(50)` | NOT NULL | Tên gói (Free, Pro, Premium, Enterprise) |
| `BaseQuotaMinutes` | `DECIMAL(10,2)` | NOT NULL | Số phút AI mặc định mỗi tháng |
| `PriceVnd` | `DECIMAL(18,2)` | NOT NULL | Giá gói cước (VNĐ) |
| `MaxParticipants` | `INT` | NOT NULL | Số người tối đa trong một phòng họp |
| `Features` | `JSONB` | NULL | Các tính năng mở khóa (PremiumVoice, Export, v.v.) |
| `IsActive` | `BOOLEAN` | DEFAULT TRUE | Trạng thái hiển thị để đăng ký |
| `CreatedAt` | `TIMESTAMP` | NOT NULL | |
| `UpdatedAt` | `TIMESTAMP` | NULL | |

### 2.2. Bảng `UsageQuotas` (Quản lý hạn ngạch)

Bảng cốt lõi chịu High-Frequency Updates từ các deduct request của AI Worker. Bắt buộc dùng Optimistic Concurrency.

| Column Name | Data Type | Constraint | Description |
|---|---|---|---|
| `Id` | `UUID` | PK | Mã định danh bản ghi hạn ngạch |
| `WorkspaceId` | `UUID` | UNIQUE, NOT NULL | Workspace/Host sở hữu |
| `PlanId` | `UUID` | FK, NOT NULL | Đang ở gói cước nào (Link tới `SubscriptionPlans`) |
| `TotalAllocatedMinutes`| `DECIMAL(10,2)` | NOT NULL | Tổng số phút được cấp (Base + TopUp) |
| `ConsumedMinutes` | `DECIMAL(10,2)` | NOT NULL, DEFAULT 0 | Tổng số phút đã xài |
| `RemainingMinutes` | `DECIMAL(10,2)` | Computed/NOT NULL | = TotalAllocated - Consumed |
| `CycleStartDate` | `TIMESTAMP` | NOT NULL | Ngày bắt đầu chu kỳ tính cước hiện tại |
| `CycleEndDate` | `TIMESTAMP` | NOT NULL | Ngày hết hạn chu kỳ (reset base quota) |
| `RowVersion` | `BYTEA / xmin`| CONCURRENCY | Token để EF Core chặn Race Condition (Optimistic Locking) |

### 2.3. Bảng `Transactions` (Giao dịch nạp tiền)

Lưu trữ thông tin từ hệ thống thanh toán PayOS.

| Column Name | Data Type | Constraint | Description |
|---|---|---|---|
| `Id` | `UUID` | PK | ID nội bộ |
| `OrderCode` | `BIGINT` | UNIQUE, NOT NULL | Mã đơn hàng sinh ra đẩy cho PayOS |
| `WorkspaceId` | `UUID` | NOT NULL | Khách hàng thực hiện thanh toán |
| `AmountVnd` | `DECIMAL(18,2)` | NOT NULL | Số tiền thanh toán |
| `PurchasedMinutes` | `DECIMAL(10,2)` | NOT NULL | Số phút AI tương ứng mua thêm |
| `Status` | `VARCHAR(20)` | NOT NULL | `PENDING`, `PROCESSING`, `SUCCESS`, `FAILED` |
| `PayOsTransactionId` | `VARCHAR(100)` | NULL | Mã giao dịch phía cổng thanh toán trả về |
| `CreatedAt` | `TIMESTAMP` | NOT NULL | |
| `CompletedAt` | `TIMESTAMP` | NULL | Thời điểm nhận Webhook thành công |

### 2.4. Bảng `QuotaAuditLogs` (Nhật ký đối soát)

Mọi thao tác thay đổi số lượng Quota đều phải insert 1 dòng vào bảng này để truy vết. Thiết kế dạng Append-Only.

| Column Name | Data Type | Constraint | Description |
|---|---|---|---|
| `Id` | `UUID` | PK | |
| `WorkspaceId` | `UUID` | INDEX | Workspace bị tác động |
| `Action` | `VARCHAR(20)` | NOT NULL | `ALLOCATE`, `DEDUCT`, `REFUND`, `EXPIRE` |
| `Amount` | `DECIMAL(10,2)` | NOT NULL | Số phút cộng/trừ (Dương là cộng, Âm là trừ) |
| `BalanceAfter` | `DECIMAL(10,2)` | NOT NULL | Số dư sau khi thực hiện Action |
| `ReferenceId` | `VARCHAR(100)` | INDEX | Idempotency Key, SessionId hoặc TransactionId |
| `Description` | `VARCHAR(255)` | NULL | Diễn giải (VD: "Trừ 5 phút cho Session ABC") |
| `CreatedAt` | `TIMESTAMP` | NOT NULL | |

---

## 3. Indexes & Performance Optimization

- `IDX_UsageQuotas_WorkspaceId`: Truy vấn lấy số dư cực nhanh khi Gateway check quyền.
- `IDX_QuotaAuditLogs_ReferenceId`: Để chặn Idempotency (kiểm tra xem đã có Audit Log cho `ReferenceId` này chưa).
- `IDX_Transactions_OrderCode`: Phục vụ luồng Webhook tìm kiếm Transaction.

## 4. Entity Framework Core Mapping
- Các kiểu `DECIMAL` sẽ được mapping với độ chính xác `(18,4)` trong DB để tránh sai số khi chia thời gian mili-giây.
- `UsageQuotas.RowVersion` cấu hình `IsRowVersion()` hoặc dùng concurrency token tương đương của PostgreSQL (`xmin`).
