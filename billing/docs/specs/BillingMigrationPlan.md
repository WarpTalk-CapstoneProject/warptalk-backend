# Feature Specification: Database Migration Plan (WT-09)

**Feature Branch**: `feature/wt-9-xay-dung-billing-quota-service-thanh-toan-va-quan-ly-dung`  
**Created**: 2026-04-29  
**Status**: Draft  
**Input**: Issue WT-09 – Phase 2 Design  

---

## 1. Migration Strategy Overview

WarpTalk sử dụng Entity Framework Core (Code-First) để quản lý Schema. 
Vì Billing Service là service hoàn toàn mới (Greenfield), việc thiết lập cơ sở dữ liệu sẽ không ảnh hưởng tới dữ liệu cũ. Tuy nhiên, cần có chiến lược Seeding Data cho các gói cước mặc định.

### Công cụ sử dụng
- **ORM**: Entity Framework Core 8+
- **Provider**: Npgsql.EntityFrameworkCore.PostgreSQL
- **Migration CLI**: `dotnet ef migrations add` & `dotnet ef database update`

---

## 2. Migration Phases

### 2.1. Initial Schema Creation (`InitialCreate`)
Tạo các bảng cơ sở dựa trên Entity classes đã định nghĩa trong thư mục `Domain/Entities`:
- `SubscriptionPlans`
- `UsageQuotas`
- `Transactions`
- `QuotaAuditLogs`

**Rủi ro/Lưu ý**:
- Chú ý cấu hình `RowVersion` thành kiểu dữ liệu phù hợp của Postgres (thường dùng concurrency token là cột hệ thống `xmin` hoặc một trường số nguyên tự tăng thông qua EF Core `.IsRowVersion()`).

### 2.2. Data Seeding (`SeedDefaultPlans`)
Ngay khi DB được tạo, cần seed 4 gói cước cơ bản (Free, Pro, Premium, Enterprise) vào bảng `SubscriptionPlans`.
- Được thực hiện tự động trong method `OnModelCreating` sử dụng `HasData(...)`.

```csharp
modelBuilder.Entity<SubscriptionPlan>().HasData(
    new SubscriptionPlan { Id = Guid.Parse("..."), Name = PlanType.Free, BaseQuotaMinutes = 30, PriceVnd = 0 },
    new SubscriptionPlan { Id = Guid.Parse("..."), Name = PlanType.Pro, BaseQuotaMinutes = 500, PriceVnd = 199000 },
    new SubscriptionPlan { Id = Guid.Parse("..."), Name = PlanType.Premium, BaseQuotaMinutes = 1000, PriceVnd = 499000 }
);
```

### 2.3. Indexes Configuration
Đảm bảo Migration bao gồm cấu hình các non-clustered index:
- `CREATE UNIQUE INDEX IX_UsageQuotas_WorkspaceId ON "UsageQuotas" ("WorkspaceId");`
- `CREATE INDEX IX_QuotaAuditLogs_ReferenceId ON "QuotaAuditLogs" ("ReferenceId");`
- `CREATE UNIQUE INDEX IX_Transactions_OrderCode ON "Transactions" ("OrderCode");`

---

## 3. Rollback Plan

Trong trường hợp deployment thất bại hoặc phát hiện lỗi nghiêm trọng trên Production:

1. **DB Rollback**:
   - Do đây là bảng mới độc lập, Rollback đơn giản là drop bảng.
   - Command: `dotnet ef database update 0` (chỉ chạy khi chưa có giao dịch thật, có giao dịch thật thì không được drop, phải fix forward).
2. **Data Preservation (Fix Forward)**:
   - Nếu lỗi liên quan đến logic, không chạy script xóa cột/bảng.
   - Luôn luôn dùng approach "Fix Forward" (Tạo migration mới để sửa lỗi cấu trúc) vì dữ liệu tài chính (Quota/Transaction) không được phép rollback làm mất thông tin người dùng đã nạp tiền.

---

## 4. CI/CD Pipeline Integration

- Lệnh `dotnet ef database update` sẽ **KHÔNG** được chạy trực tiếp từ application code lúc startup ở môi trường Production (để tránh race condition nhiều instances cùng update DB).
- Migration sẽ được xuất ra file `.sql` bằng lệnh `dotnet ef migrations script` và được thực thi bởi công cụ CD (ví dụ: GitHub Actions / ArgoCD / Flyway) trước bước deploy code mới.
