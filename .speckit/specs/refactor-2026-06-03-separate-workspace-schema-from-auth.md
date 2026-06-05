# Refactor: Tách schema workspace ra khỏi auth
Date: 2026-06-03

## What is being refactored
- Tách các table liên quan đến Workspace (`workspaces`, `workspace_members`, `workspace_invitations`) từ schema `auth` sang schema `workspace` riêng biệt.
- Xóa cột `workspace_id` khỏi bảng `auth.user_roles` do các role trong phạm vi workspace (Owner, Admin, Member, External) giờ đây được quản lý trực tiếp trong `workspace.workspace_members`.
- Thêm bảng `workspace.workspace_verified_domains` để hỗ trợ tính năng Domain Verification cho Enterprise SaaS (WT-157).
- Cấu hình thêm service `workspace-service` trong `docker-compose.yml` để chạy độc lập.

## Why
- Phân tách Microservice chuẩn chỉ theo nguyên tắc Clean Architecture và Bounded Context.
- `Auth` service chỉ nên chịu trách nhiệm về Authentication, User Identity và System-level Authorization.
- `Workspace` service chịu trách nhiệm quản lý Tenant (Enterprise) và cấu hình phân quyền trong phạm vi tổ chức (Workspace-level RBAC).
- Việc gộp chung trước đây làm tăng độ phức tạp và vi phạm nguyên tắc Single Responsibility.

## What does NOT change
- Cấu trúc cốt lõi của các Service khác (`TranslationRoom`, `Transcript`, `Notification`) vẫn giữ nguyên.
- Flow đăng nhập (Authentication) và cấp phát JWT token vẫn thực hiện qua Auth Service.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)?
- [x] Communication channels unchanged (Article II)?
- [x] Tests still pass? (Sẽ được verify qua integration test sau khi refactor)
