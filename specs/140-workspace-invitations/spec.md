# Spec 140: Workspace Invitations

**Status**: approved

## Problem Statement
As a workspace owner, I want to invite teammates, and as an invited user I want to accept the invitation, so that our team can collaborate in the same workspace.

## Scope
- Create, list, accept, revoke, and expire workspace invitations.
- Define email/link/token path for invitation delivery and acceptance.
- Create or activate membership after acceptance.

> **Lưu ý Quan trọng**: Toàn bộ luồng nghiệp vụ này chỉ áp dụng cho **Business Workspace**. Personal Workspace không hỗ trợ mời thành viên.

## User Stories
1. **Owner/Admin**: Có thể tạo thư mời thành viên mới vào workspace với Role cụ thể (trừ role Owner).
2. **Owner/Admin**: Có thể xem danh sách các thư mời đang gửi.
3. **Owner/Admin**: Có thể thu hồi (revoke) thư mời nếu gửi nhầm hoặc không muốn cấp quyền nữa.
4. **Invited User**: Có thể xem trước thông tin thư mời (Workspace Name, Role, Email) mà không cần đăng nhập.
5. **Invited User**: Có thể chấp nhận thư mời sau khi đăng nhập đúng tài khoản email được mời, qua đó trở thành Workspace Member.

## Business Rules
- **Workspace Type Rule**: Chỉ Business Workspace mới được phép mời thành viên.
- **Role Hierarchy**: 
  - Owner được phép mời thành viên và chỉ định các role: Admin, Member.
  - Admin được phép mời thành viên và chỉ định role: Admin, Member (KHÔNG thể mời người khác thành Owner).
- **Duplicate/Pending Rule**: Nếu gửi lại thư mời (resend) cho một email đang có trạng thái `PENDING`, thư mời cũ sẽ chuyển trạng thái thành `REPLACED` và một thư mời mới sẽ được tạo đè lên.
- **Invitation Statuses**:
  - `PENDING`: Đã tạo, chưa được accept, còn hiệu lực.
  - `ACCEPTED`: Đã được accept và đã tạo/kích hoạt quyền truy cập.
  - `REVOKED`: Bị owner/admin thu hồi trước khi accept.
  - `EXPIRED`: Hết hạn (7 ngày sau khi tạo), không còn dùng được.
  - `REPLACED`: Thư mời cũ bị thay thế khi resend cho cùng email.
- **Email Matching Rule**: Email của user đăng nhập vào hệ thống bắt buộc phải **khớp 100%** với email được ghi trong thư mời thì mới được accept.
- **Language Logic (Email)**:
  1. Lấy ngôn ngữ `PreferredLanguage` của recipient nếu đã có tài khoản.
  2. Fallback sang `DefaultLanguage` của Workspace.
  3. Fallback cuối cùng là `en`.

## Acceptance Criteria
- [ ] Gửi lời mời tới Business Workspace thành công.
- [ ] Gửi lời mời tới Personal Workspace trả về lỗi HTTP 403 Forbidden.
- [ ] Gửi lời mời lần 2 tới cùng email sẽ tạo token mới và cập nhật trạng thái thư cũ thành `REPLACED`.
- [ ] Public Preview API trả về dữ liệu an toàn mà không cần Bearer token.
- [ ] Tham số Role khi Invite được validate đúng phân cấp (Admin không thể gán Owner).
- [ ] Accept Invitation API từ chối nếu User đang đăng nhập có Email khác với thư mời.
- [ ] Accept Invitation thành công sẽ tạo `WorkspaceMember` mới và đổi trạng thái thành `ACCEPTED`.
- [ ] List API và Revoke API hoạt động đúng với quyền Owner/Admin.
