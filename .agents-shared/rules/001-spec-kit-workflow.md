# Spec Kit Workflow Rules (Official GitHub Spec Kit)

**Mục đích**: File rule này định nghĩa các Use Case chuẩn khi sử dụng bộ công cụ `spec-kit` chính thức của GitHub. Là một AI Agent (Gemini), khi làm việc trong workspace này, bạn bắt buộc phải tuân thủ các quy trình phát triển theo hướng Spec-Driven Development (SDD) thông qua các lệnh Slash Commands (hiện đã được cài đặt tự động bởi `specify init`).

## Các Use Cases Cốt Lõi

### 1. Khởi tạo & Thấu hiểu Luật lệ (Constitution)
- **Use Case**: Bắt đầu một dự án mới, hoặc khi có thành viên mới tham gia dự án, hoặc trước khi tiến hành code các feature quan trọng.
- **Lệnh tương ứng**: `/speckit.constitution`
- **Hành động của AI**: Đọc và ghi nhớ toàn bộ các nguyên tắc (Architecture, Coding Standards, Database Rules) trong file `.speckit/memory/constitution.md`. Không được phép viết code vi phạm các nguyên tắc này.

### 2. Định nghĩa Tính năng (Specification)
- **Use Case**: Bắt đầu làm một tính năng (`feat/*`) mới chưa từng tồn tại.
- **Lệnh tương ứng**: `/speckit.specify`
- **Hành động của AI**: Tạo ra một bản đặc tả kỹ thuật chi tiết. Bản đặc tả này phải phân định rõ "User Stories", "Acceptance Criteria" và các "Edge Cases". Chỉ khi User duyệt bản này thì mới được chuyển sang bước tiếp theo.

### 3. Lên Kế Hoạch Kỹ Thuật (Planning)
- **Use Case**: Đã có Spec hoàn chỉnh, cần vạch ra cách hiện thực hóa bằng code.
- **Lệnh tương ứng**: `/speckit.plan`
- **Hành động của AI**: Đề xuất các file cần sửa, các class/module cần tạo mới. Đảm bảo kế hoạch này tuân thủ đúng kiến trúc Microservices/Lakehouse đã đề ra.

### 4. Chia Nhỏ Công Việc (Tasks)
- **Use Case**: Kế hoạch đã được duyệt, cần tạo To-do list để theo dõi tiến độ.
- **Lệnh tương ứng**: `/speckit.tasks`
- **Hành động của AI**: Sinh ra file `tasks.md` với các ô checkbox `[ ]` để tracking. Theo dõi tiến trình bằng cách check `[x]` vào các task đã làm.

### 5. Thực Thi Kế Hoạch (Implementation)
- **Use Case**: Bắt tay vào viết code thực tế.
- **Lệnh tương ứng**: `/speckit.implement`
- **Hành động của AI**: Đọc checklist trong `tasks.md` và tiến hành code tuần tự. Đảm bảo "Viết Test trước khi viết Code" nếu Constitution yêu cầu.

## Use Cases Nâng Cao (QA & Refinement)

- **Clarify (Làm rõ)**: Dùng lệnh `/speckit.clarify` khi đọc Spec thấy có điểm mập mờ, thiếu logic. AI phải hỏi lại User chứ không được tự suy đoán bừa.
- **Analyze (Phân tích chéo)**: Dùng lệnh `/speckit.analyze` để kiểm tra xem Code hiện tại có đang đi chệch hướng so với Spec ban đầu hay không.

> **Note dành cho Agent**: Hãy quên thư mục `.agents/skills/sdd-workflow` cũ đi. Từ nay, hãy sử dụng các file markdown nằm trong thư mục `.gemini/` (được sinh ra bởi Spec Kit) để điều hướng tư duy SDD.
