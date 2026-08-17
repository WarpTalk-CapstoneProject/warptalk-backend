# Bảng So Sánh Main Flow 1 & Kịch Bản Demo (DEMO-SCRIPT.md)

Tài liệu so sánh giữa sơ đồ **Main Flow 1 (Workspace Setup)** hiện tại và **Kịch bản Demo bảo vệ (`DEMO-SCRIPT.md`)**, kèm danh sách **Icon & Action dự kiến bổ sung** vào sơ đồ quy trình Main Flow 1.

---

## 📊 Bảng So Sánh Chi Tiết & Đề Xuất Bổ Sung Icon

| STT | Luồng / Hành động (Action) | Sơ đồ Hiện tại (Main Flow 1) | Kịch bản Demo Script (`DEMO-SCRIPT.md`) | Icon & Action dự kiến bổ sung vào Diagram |
|---|---|---|---|---|
| 1 | **Mua gói dịch vụ** *(Buy Subscription)* | ❌ Chưa có (bắt đầu thẳng từ Create Workspace) | 🟢 Đăng ký/Đăng nhập $\rightarrow$ Thanh toán gói dịch vụ (Plan) trên Landing Page trước khi tạo Workspace | 💳 **`Buy Subscription Plan`** *(Ví icon Thẻ/Thanh toán)* |
| 2 | **Khởi tạo Workspace** *(Create Workspace)* | ✅ `Create Workspace` $\rightarrow$ `Setting workspace` | ✅ `Create Workspace` (`/workspace/create`): Nhập Tên + Logo $\rightarrow$ Thiết lập hệ thống | ⚙️ **`Setting Workspace`** *(Icon Bánh răng/Cấu hình - Đã có)* |
| 3 | **Cấu hình Workspace Settings & Policies** *(Configure WS Settings & Policies)* | ❌ Chưa có | 🟢 Thiết lập danh sách ngôn ngữ cho phép (*Allowed Languages: Việt + Anh*), chính sách AI/DLP, và bảng thuật ngữ (*Glossary*) | 🌐 **`Configure WS Settings & Policies`** *(Icon Quả địa cầu/Cấu hình chính sách)* |
| 4 | **Mời thành viên & Gửi Email** *(Invite Members & Send Email)* | ✅ `Invite Members` $\rightarrow$ `Send Invitation Email` | ✅ `Invite Members` (`/[slug]/members`): Hệ thống **kiểm tra Enforce Quota** (chặn nếu vượt quá seat limit) $\rightarrow$ Gửi Email mời | ✉️ 📩 **`Send Invitation Email (Check Enforce Quota)`** *(Icon Phong bì - Đã tích hợp Enforce Quota)* |
| 5 | **Chấp nhận lời mời & Gia nhập** *(Accept Invitation & Join)* | ✅ `Accept Invitation & Join Workspace` | ✅ Invitee mở email bấm chấp nhận lời mời (hoặc Owner duyệt dòng Requested) $\rightarrow$ Gia nhập Workspace | 👥 [JOIN] **`Accept Invitation & Join Workspace`** *(Giữ icon JOIN nhóm người có sẵn)* |
| 6 | **Thêm thành viên vào Workspace** *(Add Member)* | ✅ `Add Member to Workspace` | ✅ Hệ thống tự động thêm người dùng đã được duyệt/accept vào danh sách thành viên active | 👤+ **`Add Member to Workspace`** *(Icon Thêm người - Đã có)* |
| 7 | **Tải & Xử lý Tài liệu Tri thức** *(Upload & Process Docs)* | ✅ `Upload Workspace Documents` $\rightarrow$ `Process & Store Documents` | ✅ Upload PDF/DOCX (`/documents`) $\rightarrow$ Hệ thống học dữ liệu RAG và lưu kho tri thức (`/knowledge`) | 🧠 **`Process & Store Knowledge Base`** *(Icon Não bộ/Tri thức - Đã có)* |

---

## 🎯 Tóm Tắt Đề Xuất Cập Nhật Diagram Main Flow 1

Để sơ đồ **Main Flow 1** bám sát 100% kịch bản **DEMO-SCRIPT.md**, sơ đồ mới nên bổ sung các bước hành động quan trọng sau:

1. 💳 **Buy Subscription Plan** *(Bước đầu tiên trước Create Workspace)*
2. 🌐 **Configure WS Settings & Policies** *(Bước cấu hình chính sách, ngôn ngữ & glossary trước khi mời người)*
3. 📩 **Send Invitation Email (Check Enforce Quota)** *(Tích hợp kiểm tra giới hạn ghế của gói ngay khi gửi Email mời)*
4. 👥 [JOIN] **Accept Invitation & Join Workspace** *(Giữ nguyên icon JOIN nhóm người có sẵn)*
