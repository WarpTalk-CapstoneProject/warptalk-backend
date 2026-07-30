# Technical Debt: Access Control for Room History & Artifacts

## Question / Issue
API lịch sử phòng và link tải file (Artifacts) sẽ phân quyền như thế nào?

## Details
* Chỉ có **Host** (người tạo phòng) được phép truy cập lịch sử và tải file?
* Hay toàn bộ **Participant** đã từng nhấn tham gia phòng đó (nằm trong bảng `translation_room_participants` với trạng thái `CONNECTED` / `LEFT` / `DISCONNECTED`) đều được quyền xem lịch sử và tải transcript/summary?
* Hay quyền này mở rộng cho mọi thành viên trong cùng **WorkspaceId**?

## Backlog / Resolution Strategy
* Hiện tại trong giai đoạn 1 (WT-68), chúng ta sẽ mặc định phân quyền cơ bản: **Chỉ cho phép Host của phòng truy cập**.
* Các chính sách phân quyền chi tiết hơn (cho Participant hoặc Workspace) sẽ được tách thành một ticket tối ưu hóa phân quyền riêng để xử lý sau khi có yêu cầu rõ ràng từ PO/Product team.
