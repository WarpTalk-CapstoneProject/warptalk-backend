# Screen Flow PlantUML Specifications

Thư mục này chứa toàn bộ mã nguồn sơ đồ **Screen Flow** chuẩn UML cho hệ thống WarpTalk, đối soát 100% với các Route Path thực tế trong mã nguồn Frontend `@warptalk-web` và các file đặc tả `specs/`.

---

## Cấu trúc thư mục theo Module:

```
.agents/resources/screen flow/
├── auth/
│   ├── 01-auth-screen-flow.puml
│   └── 02-user-settings-preferences-screen-flow.puml
├── workspace/
│   ├── 01-workspace-selection-and-creation-screen-flow.puml
│   ├── 02-workspace-settings-and-members-screen-flow.puml
│   └── 03-workspace-documents-screen-flow.puml
├── billing/
│   ├── 01-first-time-subscription-screen-flow.puml
│   ├── 02-recurring-subscription-screen-flow.puml
│   └── 03-credit-topup-screen-flow.puml
├── meeting/
│   └── 01-unified-meeting-and-translation-room-screen-flow.puml
├── transcript/
│   └── 01-transcript-and-ai-summaries-screen-flow.puml
├── notification/
│   └── 01-notification-settings-screen-flow.puml
└── README.md
```

---

## Chi tiết các Module:

### 1. Module Auth (`auth/`)
- **01-auth-screen-flow.puml**: Đăng nhập, Đăng ký, Quên mật khẩu, Xác thực Email, Chấp nhận lời mời.
- **02-user-settings-preferences-screen-flow.puml**: Cài đặt tài khoản cá nhân & tùy chọn ngôn ngữ/giao diện.

### 2. Module Workspace (`workspace/`)
- **01-workspace-selection-and-creation-screen-flow.puml**: Luồng chuyển đổi Workspace (Switch Workspace) trực tiếp từ Sidebar Dropdown (đối soát đặc tả WT-346), Tạo Workspace mới và Trang danh sách Workspace.
- **02-workspace-settings-and-members-screen-flow.puml**: Cài đặt Workspace & Quản lý/Mời thành viên.
- **03-workspace-documents-screen-flow.puml**: Quản lý tài liệu RAG Workspace Documents (Upload, Ingestion, View Chunks).

### 3. Module Billing (`billing/`)
- **01-first-time-subscription-screen-flow.puml**: Mua gói dịch vụ lần đầu.
- **02-recurring-subscription-screen-flow.puml**: Mua/Nâng cấp gói từ tháng thứ 2.
- **03-credit-topup-screen-flow.puml**: Nạp thêm AI Credits (Top Up).

### 4. Module Meeting, Translation Room & WarpBot Real-time (`meeting/`)
- **01-unified-meeting-and-translation-room-screen-flow.puml**: Tạo cuộc họp ➔ Preflight Check & Cấu hình thiết bị ➔ Phòng chờ (Lobby) ➔ Phòng họp chính (**Live Subtitles**, **In-Meeting Translated Chat Panel**, **Live Transcript Panel**, **WarpBot Real-Time Context Panel**) ➔ Kết thúc họp ➔ Xem Artifacts & Transcript.

### 5. Module Transcript & AI (`transcript/`)
- **01-transcript-and-ai-summaries-screen-flow.puml**: Tra cứu Transcript, Lịch sử cuộc họp và AI Summaries.

### 6. Module Notification (`notification/`)
- **01-notification-settings-screen-flow.puml**: Trung tâm thông báo & tùy chỉnh kênh nhận thông báo.
