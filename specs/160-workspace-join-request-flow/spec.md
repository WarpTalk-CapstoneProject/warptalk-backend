# Feature Specification: Workspace Join Request & Room Preflight Governance (WT-160)

**Feature Branch**: `feat/workspace-join-request-flow`  
**Created**: 2026-06-21  
**Status**: Draft  
**Input**: User description: "frontend hiển thị không có quyền join meeting, gửi yêu cầu tới admin... preview room bằng code biết room thuộc workspace nào... login/register tối giản email/pass... backend handle kiểu khác không thêm bảng mới."

---

## 1. Problem Statement & Context

### 1.1. Problem Statement
Trong mô hình cộng tác bảo mật của WarpTalk, các cuộc họp dịch cabin trực tuyến (`TranslationRoom`) thuộc sở hữu của một `Enterprise Workspace`. Hiện tại, nếu người dùng nhận được link phòng họp dạng `https://warptalk.app/join?code=XYZ` nhưng tài khoản của họ **chưa** gia nhập workspace sở hữu phòng họp đó:
1. **Thiếu thông tin:** Trang `/join` hiện tại không có cơ chế tìm kiếm xem phòng họp thuộc workspace nào để cảnh báo trước khi join.
2. **Trải nghiệm bị chặn cứng:** Hệ thống chỉ trả về lỗi 403/400 khi bấm Join, không cung cấp giải pháp cho người dùng gửi yêu cầu phê duyệt tham gia workspace.
3. **Quy trình đăng ký cồng kềnh:** Khách truy cập chưa có tài khoản phải đi qua luồng đăng ký tài khoản đầy đủ, trong khi họ chỉ cần một form xác thực tối giản (chỉ gồm email/username và password) để hệ thống tự động hoàn thành xác thực/đăng ký ở nền.

Để tối ưu hóa trải nghiệm và bảo vệ ranh giới bảo mật B2B, hệ thống cần triển khai **Workspace Join Request Flow** ngay tại màn hình chuẩn bị vào phòng họp.

---

## 2. Technical Decisions & Backend Design (No New Tables)

Để tuân thủ yêu cầu **không thêm bảng cơ sở dữ liệu mới**, hệ thống sẽ tái sử dụng cấu trúc bảng hiện có:

### 2.1. Tái sử dụng bảng `WorkspaceInvitation` cho Join Requests
Bảng `WorkspaceInvitation` (hiện có các cột `WorkspaceId`, `Email`, `RoleId`, `MembershipType`, `Status`, `InvitedBy`, `TokenHash`) sẽ được tận dụng để lưu trữ các yêu cầu xin gia nhập từ phía người dùng:
1. **Trạng thái mới:** Bổ sung trạng thái `REQUESTED` vào enum `InvitationStatus` (trong `WarpTalk.WorkspaceService.Domain`).
2. **Khai báo yêu cầu:**
   * **`Status`**: `REQUESTED` (Biểu thị đây là yêu cầu xin gia nhập từ user, không phải lời mời từ admin).
   * **`Email`**: Email của người dùng gửi yêu cầu.
   * **`RoleId`**: Mặc định gán `RoleId` ứng với vai trò `Member` trong workspace.
   * **`MembershipType`**: Bắt buộc gán `Internal`.
   * **`InvitedBy`**: Lưu `UserId` của chính người dùng gửi yêu cầu (người tự đề xuất).
   * **`TokenHash`**: Lưu một chuỗi băm dummy/ngẫu nhiên (không dùng để accept thủ công qua token link).
   * **`ExpiresAt`**: Mặc định hết hạn sau 7 ngày kể từ khi tạo.

3. **Chính sách Bảo mật & Chống Spam (Spam & Harassment Prevention Policy):**
   * **Quy tắc chặn cứng:** Người dùng chỉ được phép gửi yêu cầu xin gia nhập (`REQUESTED`) nếu tên miền email của họ **trùng khớp** với danh sách tên miền đã được xác minh (`VerifiedDomains`) của Workspace đó.
   * **Đối tác bên ngoài (External Users/Public Emails):** Các tài khoản sử dụng email cá nhân (như `@gmail.com`, `@yahoo.com`) hoặc email thuộc tên miền chưa được xác minh của workspace **sẽ bị chặn hoàn toàn**, không được phép tạo yêu cầu xin gia nhập.
   * **Lý do:** Đối tác ngoài (External Collaborator) bắt buộc phải do Admin/Owner chủ động gửi lời mời trực tiếp (invitation flow), không được tự ý gửi yêu cầu xin tham gia để tránh việc Admin bị quấy rối hoặc vô tình phê duyệt các tài khoản lạ vào vùng dữ liệu của doanh nghiệp.
   * **Xử lý Backend:** API `POST /api/v1/workspaces/{workspaceId}/join-requests` sẽ kiểm tra tên miền của người yêu cầu. Nếu không trùng khớp với bất kỳ domain verified nào của workspace, hệ thống trả về lỗi `403 Forbidden` (mã lỗi `ValidationError` hoặc `CannotRequestJoinExternalDomain`).
   * **Xử lý Frontend:** Căn cứ vào API Preflight, nếu email hiện tại của người dùng không trùng khớp domain của workspace, nút "Gửi yêu cầu" sẽ bị ẩn/vô hiệu hóa, thay bằng hướng dẫn liên hệ trực tiếp với ban tổ chức.

### 2.2. API Preview cuộc họp bằng Code (Room Preflight Preview) & Bảo mật Enterprise
Phát triển endpoint mới trong `TranslationRoomService` để cung cấp thông tin xem trước tối thiểu của phòng họp, đồng thời áp dụng các nguyên tắc bảo mật chặt chẽ cho doanh nghiệp:

1. **Giải quyết Workspace tự động ở Server-side (Server-side workspace resolution):**
   * Endpoint tạo yêu cầu gia nhập `POST /api/v1/workspaces/join-requests` sẽ hoạt động ở chế độ authenticated.
   * Client **không** truyền `workspaceId` trực tiếp. Thay vào đó, backend tự động phân giải `roomCode` hoặc `workspaceSlug` từ request body để xác định workspace sở hữu và các admin/host liên quan để tạo request. Điều này giúp kiểm soát ranh giới dữ liệu và bảo mật chặt chẽ hơn.

2. **Rate Limit mạnh mẽ endpoint public preflight tại API Gateway:**
   * Endpoint `GET /api/v1/translation-rooms/preflight/{roomCode}` được thiết lập là public (`[AllowAnonymous]`).
   * Để chống spam và khai thác dò quét phòng, API Gateway sẽ áp dụng policy rate-limit giới hạn tối đa **10 requests / phút** trên mỗi IP/thiết bị.

3. **Trả về mã lỗi 404 Not Found generic:**
   * Để ngăn chặn hành vi dò tìm (enumeration) mã phòng hoặc thông tin workspace, hệ thống sẽ trả về lỗi `404 Not Found` generic và duy nhất (`"Translation room not found or unavailable."`) cho tất cả các trường hợp: mã phòng không tồn tại, mã phòng hết hạn, hoặc workspace sở hữu phòng đang không hoạt động (`IsActive == false` hoặc `DeletedAt != null`). Kẻ tấn công không thể phân biệt giữa phòng thực tế bị hết hạn hay phòng không tồn tại.

4. **Không lộ thông tin Workspace nhạy cảm (Workspace Privacy Boundary):**
   * Preflight API chỉ trả về thông tin `WorkspaceName` và `WorkspaceSlug` nếu thỏa mãn một trong các điều kiện:
     * User hiện tại đã là member của Workspace đó.
     * Email domain của user khớp với các verified domains của Workspace.
     * Workspace cấu hình cho phép cộng tác bên ngoài (`AllowExternalCollaboration == true`).
   * Nếu không thỏa mãn các điều kiện trên, trường `WorkspaceName` và `WorkspaceSlug` sẽ được trả về là `null` hoặc chuỗi rỗng để tránh rò rỉ tên doanh nghiệp ra bên ngoài.

* **Preflight DTO chi tiết:**
  * `RoomCode` (string)
  * `RequiresJoinRequest` (bool)
  * `IsUserMember` (bool)
  * `IsDomainMatched` (bool)
  * `AllowExternalCollaboration` (bool)
  * `WorkspaceName` (string, nullable)
  * `WorkspaceSlug` (string, nullable)
  * `IsAuthenticated` (bool)


---

## 3. User Scenarios & Testing (Prioritized Journeys)

### Quy tắc kiểm tra trạng thái hoạt động của Workspace (Workspace Active State Verification):
> [!IMPORTANT]
> Trong tất cả các kịch bản hành trình của người dùng, hệ thống **bắt buộc phải kiểm tra trạng thái hoạt động** của Workspace. Workspace đích phải thỏa mãn điều kiện:
> - `IsActive == true`
> - `DeletedAt == null`
>
> **Thời điểm kiểm tra:**
> 1. Ngay khi người dùng nhập slug/URL của workspace (bước phân giải slug tại Backend).
> 2. Ngay khi gọi API Preflight lấy thông tin phòng họp (bước phân giải meeting code).
> 3. Tầng Backend trước khi chèn bản ghi yêu cầu gia nhập vào bảng `WorkspaceInvitation`.
>
> Nếu workspace không hoạt động hoặc đã bị xóa mềm, hệ thống phải chặn đứng hành động và hiển thị thông báo lỗi rõ ràng: *"Workspace không hoạt động hoặc không tồn tại."*

---

### User Story 1 - Join Workspace via Slug/URL for Authenticated Users (Priority: P1)
*Là một người dùng đã đăng nhập vào hệ thống và đang ở cổng onboarding gateway (hoặc sidebar), tôi muốn chọn "Join Workspace", nhập slug hoặc URL của workspace cần tham gia (ví dụ: `acme`), hệ thống kiểm tra và cho phép tôi gửi yêu cầu tham gia (Join Request) đến Admin thay vì bắt buộc tôi phải tự tạo một workspace mới.*

* **Why this priority**: Core user journey for authenticated onboarding. Cho phép người dùng kết nối với tổ chức của họ một cách linh hoạt.
* **Independent Test**: Người dùng đã đăng nhập truy cập `/workspace/join`, nhập slug `acme`. Hệ thống xác thực domain và tạo join request thành công.
* **Acceptance Scenarios**:
  1. **Given** một người dùng đã đăng nhập và truy cập trang `/workspace/join`,  
     **When** người dùng nhập slug của workspace `acme` (đang hoạt động và khớp domain email của user),  
     **Then** hệ thống hiển thị nút **"Send Join Request to Workspace Admin"**.
  2. **Given** người dùng nhập slug của workspace `acme-inactive` (có `IsActive == false` hoặc `DeletedAt != null`),  
     **When** hệ thống kiểm tra slug,  
     **Then** hệ thống hiển thị thông báo lỗi: *"Workspace không hoạt động hoặc không tồn tại."* và chặn không cho gửi request.

---

### User Story 2 - Redirected to Workspace Join via Meeting Link (Priority: P1)
*Là một người dùng nhận được link phòng họp (`join?code=XYZ`) nhưng chưa là thành viên của Workspace sở hữu phòng họp đó, tôi muốn hệ thống tự động chuyển hướng tôi sang trang `/workspace/join?code=XYZ` để hoàn thành đăng nhập tối giản (nếu chưa đăng nhập) và gửi yêu cầu gia nhập Workspace trong nền.*

* **Why this priority**: Đảm bảo định tuyến đúng ranh giới bảo mật của Workspace khi có liên kết phòng họp.
* **Independent Test**: Khách chưa thuộc workspace truy cập link cuộc họp. Hệ thống tự động chuyển hướng sang `/workspace/join` để xác thực tối giản và xin phê duyệt.
* **Acceptance Scenarios**:
  1. **Given** một cuộc họp `WARP-123` thuộc workspace `Acme` (đang hoạt động),  
     **When** người dùng (chưa join Acme) truy cập `/join?code=WARP-123`,  
     **Then** hệ thống gọi API Preflight, phát hiện user chưa phải member và tự động redirect người dùng sang `/workspace/join?code=WARP-123`.
  2. **Given** cuộc họp `WARP-123` thuộc workspace `Acme-inactive` (ngưng hoạt động hoặc đã bị xóa),  
     **When** gọi API Preflight hoặc truy cập link họp,  
     **Then** hệ thống hiển thị thông báo lỗi: *"Phòng họp hoặc Workspace không hoạt động."* và chặn không cho truy cập hay chuyển hướng gửi request.
  3. **Given** người dùng chưa đăng nhập và được redirect sang `/workspace/join?code=WARP-123`,  
     **When** trang được load,  
     **Then** hiển thị thông báo chặn cùng form đăng nhập tối giản. Sau khi đăng nhập thành công và khớp domain, hiển thị nút **"Send Join Request to Workspace Admin"** mà không lộ tên phòng họp ra UI.

---

### User Story 3 - Admin Approve / Reject Join Request (Priority: P2)
*Là Admin của Workspace, tôi muốn xem các yêu cầu xin gia nhập và phê duyệt/từ chối chúng.*

* **Why this priority**: Hoàn thiện vòng đời của luồng Join Request.
* **Independent Test**: Admin nhấn "Approve" trên màn hình quản lý lời mời. Bản ghi chuyển thành `ACCEPTED` và người yêu cầu trở thành `Internal Member`.
* **Acceptance Scenarios**:
  1. **Given** một yêu cầu join đang ở trạng thái `REQUESTED`,  
     **When** Admin nhấn **Approve**,  
     **Then** hệ thống cập nhật bản ghi thành `ACCEPTED`, tạo mới bản ghi `WorkspaceMember` cho user đó với role `Member` và membership type `Internal`.
  2. **Given** yêu cầu join đang ở trạng thái `REQUESTED`,  
     **When** Admin nhấn **Reject/Revoke**,  
     **Then** hệ thống cập nhật status thành `REVOKED`/`REJECTED`.

---

## 4. Requirements

### 4.1. Functional Requirements

* **FR-160-001**: Hệ thống MUST cung cấp endpoint công khai `GET /api/v1/translation-rooms/preflight/{roomCode}` để trả về thông tin phòng và workspace sở hữu phòng đó.
* **FR-160-002**: Frontend trang `/join` MUST tự động gọi API Preflight khi phát hiện tham số `code` trên URL.
* **FR-160-003**: Nếu kết quả `IsUserMember` từ API Preflight trả về `false`, frontend MUST hiển thị cảnh báo: *"Bạn không có quyền join meeting này, gửi yêu cầu tới admin"* và hiển thị nút **"Send Request to Workspace Admin"**.
* **FR-160-004**: Đối với người dùng chưa đăng nhập, frontend MUST hiển thị form xác thực tối giản (chỉ chứa ô nhập Username/Email và Password) để tự động đăng nhập/đăng ký thông qua cơ chế auto-auth hiện tại của backend trước khi gửi join request.
* **FR-160-005**: Hệ thống MUST cung cấp API `POST /api/v1/workspaces/{workspaceId}/join-requests` để tạo bản ghi yêu cầu tham gia.
* **FR-160-006**: API tạo join request MUST lưu dữ liệu vào bảng `WorkspaceInvitation` với trạng thái `REQUESTED`, `MembershipType = Internal` và `RoleId` ứng với vai trò `Member`.
* **FR-160-007**: Hệ thống MUST chặn người dùng gửi yêu cầu trùng lặp nếu đã có bản ghi `REQUESTED` hoặc `PENDING` đang hoạt động cho cùng email/workspace.
* **FR-160-008**: Hệ thống MUST cho phép Admin duyệt yêu cầu qua API `POST /api/v1/workspaces/{workspaceId}/invitations/{invitationId}/approve` hoặc tích hợp vào hàm Accept của lời mời để chuyển trạng thái thành `ACCEPTED` và kích hoạt tài khoản làm `Internal Member`.
* **FR-160-009**: Trang quản lý truy cập dành cho Admin (`/workspace/invitations`) MUST gộp chung cả Lời mời đã gửi (outbound) và Yêu cầu gia nhập (inbound) vào một giao diện duy nhất, phân chia trực quan bằng 2 Tabs: "Lời mời đã gửi" (Outbound) và "Yêu cầu gia nhập" (Inbound).
* **FR-160-010**: Bộ chọn 2 Tabs quản lý (Invitations/Outbound và Join Requests/Inbound) ở page quản lý `/workspace/invitations` MUST được thiết kế theo phong cách **Pills (viên thuốc)** kế thừa từ [MeetingPropertiesPills.tsx](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/warptalk-web/src/app/(app)/[workspaceSlug]/rooms/[id]/MeetingPropertiesPills.tsx):
  - **Tabs Pill Style:** Mỗi nút Tab được hiển thị như một Pill độc lập, bo tròn hoàn toàn (`rounded-full`), khi Active sẽ có nền `bg-surface-1`, viền mảnh `border border-border/60`, và đổ bóng mỏng `shadow-[0_1px_2px_rgba(0,0,0,0.02)]`. Khi Inactive có nền trong suốt (`bg-transparent`) và chữ tối mờ để tạo tương phản rõ nét.
  - **Tab Badge / Count:** Hiển thị số lượng bản ghi tương ứng bên trong Tab (nếu có) dưới dạng hình tròn nhỏ `w-5 h-5 rounded-full bg-primary/10 text-primary flex items-center justify-center text-[9px] font-bold shrink-0` cạnh text của Tab.
  - **Typography:** Đồng bộ cỡ chữ nhỏ gọn `text-[12px]` hoặc `text-[11px]` với `font-medium` để tạo cảm giác tinh gọn, hiện đại.



### 4.2. Key Entities (Reused)

* **`WorkspaceInvitation`**:
  * Trạng thái mở rộng: `InvitationStatus.REQUESTED` dùng cho yêu cầu gia nhập từ phía người dùng.
  * Cột `InvitedBy`: Lưu ID người gửi yêu cầu.
  * Cột `TokenHash`: Dummy value (ví dụ hash của chuỗi `"REQUEST-{UserId}"`).

---

## 5. Success Criteria

### Measurable Outcomes
* **SC-160-001**: API Preflight Room Preview thực hiện với thời gian phản hồi dưới 30ms tại tầng Database.
* **SC-160-002**: 100% yêu cầu gia nhập được ghi nhận chính xác vào bảng `WorkspaceInvitation` mà không phát sinh thêm bảng dữ liệu mới.
* **SC-160-003**: Người dùng sau khi được Admin phê duyệt yêu cầu sẽ tự động được đồng bộ làm `Internal Member` với role `Member`.

---

## 6. Assumptions
* Cơ chế auto-login/register của backend đã hoạt động ổn định và sẵn sàng xử lý yêu cầu xác thực tối giản từ frontend.
* Quyền admin/owner của workspace được áp dụng chuẩn xác trên các API phê duyệt/từ chối lời mời/yêu cầu.
