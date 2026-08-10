# Feature Specification: Enterprise Workspace Invitations (WT-140)

**Feature Branch**: `feat/auth`  
**Created**: 2026-05-24  
**Updated**: 2026-08-10
**Status**: Draft  
**Input**: Linear ticket WT-140 - [Workspace] Invite users to an Enterprise Workspace

---

## 1. Problem Statement

WarpTalk currently treats Workspace as an Enterprise tenant boundary. There is no non-enterprise workspace type or `WorkspaceType` branch in the Workspace Service code. Owners and Admins need a controlled way to invite internal teammates and approved external collaborators into an Enterprise Workspace while preserving tenant isolation, role boundaries, verified-domain policy, and invitation-token safety.

The invitation flow must support:

1. **Internal invitation**: invite users whose email domain is accepted by the workspace verified-domain policy.
2. **External collaboration**: invite outside-domain collaborators only when external collaboration is explicitly enabled.
3. **Invitation lifecycle**: preview, accept, revoke, expire, resend, and replace stale pending tokens.
4. **Role assignment**: assign only supported workspace roles while preventing unsafe Owner/Admin escalation.

---

## 2. Current Implementation Scope

### 2.1. Enterprise-only workspace model

- The Workspace Service does not implement non-enterprise workspace flows.
- All invitation behavior applies to Enterprise Workspaces.
- Internal/external behavior is controlled by:
  - `Workspace.AllowExternalCollaboration`
  - `Workspace.RequireVerifiedDomainForInternal`
  - `Workspace.AllowSubdomains`
  - `workspace_verified_domains`
  - `WorkspaceMember.MembershipType` (`Internal` or `External`) — set from the inviter's explicit choice, with the domain policy deciding only which choices are legal.

### 2.2. Invitation lifecycle states

Supported invitation statuses:

- `PENDING`: invitation token can be previewed and accepted.
- `ACCEPTED`: invitation has been consumed and membership was created or reactivated.
- `REVOKED`: Owner/Admin canceled the invitation.
- `EXPIRED`: token is past its expiry window.
- `REPLACED`: a newer resend token superseded the previous pending token.

### 2.3. Security model

- Invitation tokens are stored as hashes, not plaintext tokens.
- Preview endpoint exposes safe invitation metadata without leaking token hashes.
- Accept requires the authenticated user's email to match the invited email exactly.
- Role IDs must resolve through the Auth role catalog.

---

## 3. Roles and Membership Rules

### Owner

- Can invite Internal members as `Admin` or `Member`.
- Can invite External members only as `Member`.
- Can revoke and resend pending invitations.
- Can manage workspace settings that affect invitation policy.

### Admin

- Can invite Internal members as `Admin` or `Member`.
- Cannot assign or invite `Owner`.
- Can invite External members only as `Member` when external collaboration is enabled.
- Can revoke and resend pending invitations within the workspace.

### Member

- Cannot create, revoke, or resend invitations.
- Can accept an invitation addressed to their exact authenticated email.

### External Member

- Can only be invited when `AllowExternalCollaboration = true`.
- Must have role `Member`.
- Cannot be assigned `Owner` or `Admin`.
- Is invited by explicit inviter choice, not by failing a domain match. An address whose domain *is* verified may still be invited as External when that is what the inviter intends.

---

## 4. User Scenarios and Acceptance Tests

### User Story 1 - Invite an Internal Enterprise Member (Priority: P1)

*As an Owner/Admin, I want to invite an internal teammate so that they can join the Enterprise Workspace with the correct role.*

**Independent Test**: Seed an Enterprise Workspace with a verified domain. Send an invitation to an email matching that domain with membership type `Internal` and role `Member` or `Admin`. Assert that a pending invitation is created and an email event can be dispatched.

**Acceptance Scenarios**:

1. **Given** an active Owner/Admin and an email matching workspace verified-domain policy,  
   **When** they create an internal invitation,  
   **Then** the system creates a `PENDING` invitation with the requested `Admin` or `Member` role.
2. **Given** `RequireVerifiedDomainForInternal = true`,  
   **When** the invite email domain is not verified for the workspace,  
   **Then** the request is rejected with a validation error.
3. **Given** `RequireVerifiedDomainForInternal = true`,
   **When** the invite email uses a public domain (`gmail.com`, `outlook.com`, …) and `membershipType = Internal`,
   **Then** the request is rejected because a public domain can never be a verified enterprise domain.
4. **Given** `RequireVerifiedDomainForInternal = false`,
   **When** an Owner/Admin invites any email — public domain or not — as `Internal`,
   **Then** the system performs no domain validation at all and creates the `PENDING` internal invitation.
5. **Given** an email domain is already verified by another workspace,
   **When** it is used as an internal enterprise domain,  
   **Then** the domain policy validation rejects it.

---

### User Story 2 - Invite an External Collaborator (Priority: P1)

*As an Owner/Admin, I want to invite an outside-domain collaborator only when the workspace policy allows external collaboration.*

**Independent Test**: Set `AllowExternalCollaboration = true`. Send an invitation to an outside-domain email with membership type `External` and role `Member`. Assert success. Repeat with role `Admin` and assert rejection.

**Acceptance Scenarios**:

1. **Given** external collaboration is enabled,  
   **When** an Owner/Admin invites an external email as `Member`,  
   **Then** the system creates a `PENDING` external invitation.
2. **Given** external collaboration is disabled,  
   **When** an Owner/Admin invites an external user,  
   **Then** the request is rejected.
3. **Given** an external invitation requests role `Owner` or `Admin`,  
   **When** the request is validated,  
   **Then** the request is rejected because external collaborators must be `Member`.

---

### User Story 3 - Preview and Accept Invitation (Priority: P1)

*As an invited user, I want to preview and accept my invitation only with the exact invited account email.*

**Independent Test**: Create a pending invitation, call preview with token, authenticate as the same email, accept token, and assert `workspace_members` contains the active membership and invitation status is `ACCEPTED`.

**Acceptance Scenarios**:

1. **Given** a valid pending token,  
   **When** the user previews the invitation,  
   **Then** the response shows safe workspace/inviter/role metadata and not token hash.
2. **Given** the authenticated user's email exactly matches the invited email,  
   **When** the user accepts a valid pending token,  
   **Then** the invitation becomes `ACCEPTED` and an active workspace membership is created or reactivated.
3. **Given** the authenticated email does not match the invited email,  
   **When** the user attempts acceptance,  
   **Then** the request is rejected.

---

### User Story 4 - Revoke, Expire, and Resend Invitation (Priority: P2)

*As an Owner/Admin, I want to revoke or resend invitations so that stale or incorrect tokens cannot be reused.*

**Independent Test**: Create a pending invitation, revoke it, assert accept fails. Create another pending invitation for the same email and resend, assert the old token becomes `REPLACED` and the new token is the only acceptable token.

**Acceptance Scenarios**:

1. **Given** a pending invitation,  
   **When** an Owner/Admin revokes it,  
   **Then** the invitation status becomes `REVOKED` and cannot be accepted.
2. **Given** a pending invitation has passed its expiry time,  
   **When** a user attempts acceptance,  
   **Then** the invitation is treated as expired and cannot be accepted.
3. **Given** an Owner/Admin resends an invitation to the same email,  
   **When** a new token is issued,  
   **Then** the previous pending invitation becomes `REPLACED`.

---

### User Story 5 - Inviter Chooses the Membership Type (Priority: P1)

*As an Owner/Admin, I want to choose explicitly whether the person I invite joins as an Internal member or an External collaborator, so that access class is a decision I make and can see, not something the system infers from an email domain behind my back.*

**Independent Test**: Open the invite form in a workspace with `AllowExternalCollaboration = true` and one verified domain. Assert the form offers an access-type dropdown (`Internal` / `External`) pre-selected from the typed email's domain, and that changing it changes the `membershipType` sent to the API. Invite the same address once as `Internal` and once as `External` and assert the stored invitation reflects the inviter's choice both times.

**Acceptance Scenarios**:

1. **Given** the invite form,
   **When** the inviter types an email address,
   **Then** the access-type dropdown pre-selects the type the domain policy suggests, and the inviter may override it.
2. **Given** the inviter selects `External`,
   **When** the invitation is created,
   **Then** the role is forced to `Member` and the invitation is stored with `MembershipType = External`, regardless of whether the email domain happens to be verified.
3. **Given** `AllowExternalCollaboration = false`,
   **When** the invite form is opened,
   **Then** the `External` option is disabled with the reason shown, and an `External` request sent anyway is rejected by the server.
4. **Given** the inviter selects `Internal` and `RequireVerifiedDomainForInternal = true`,
   **When** the email domain is not verified for this workspace,
   **Then** the request is rejected and the form explains that the domain must be verified first.
5. **Given** an invitation was created with an explicit membership type,
   **When** it is later accepted,
   **Then** the membership is created with the type the inviter chose — the system never silently reclassifies it.

---

## 5. Functional Requirements

- **FR-140-001**: System MUST expose an Owner/Admin-only API to create Enterprise Workspace invitations.
- **FR-140-002**: System MUST require `membershipType` to be either `Internal` or `External`.
- **FR-140-003**: System MUST validate invited role through the Auth role catalog.
- **FR-140-004**: System MUST reject invitation requests that assign `Owner` through the invitation flow.
- **FR-140-005**: System MUST reject external invitations unless `AllowExternalCollaboration = true`.
- **FR-140-006**: System MUST reject external invitations with any role other than `Member`.
- **FR-140-007**: System MUST enforce verified-domain policy for internal invitations when `RequireVerifiedDomainForInternal = true`.
- **FR-140-008**: System MUST reject active duplicate membership for the same workspace and email/user.
- **FR-140-009**: System MUST hash invitation tokens at rest and never expose token hashes in API responses.
- **FR-140-010**: System MUST allow safe invitation preview by token without returning sensitive workspace internals.
- **FR-140-011**: System MUST require exact invited-email match on invitation acceptance.
- **FR-140-012**: System MUST create or reactivate a `WorkspaceMember` in the same consistency boundary as accepting the invitation.
- **FR-140-013**: System MUST support `PENDING`, `ACCEPTED`, `REVOKED`, `EXPIRED`, and `REPLACED` invitation states.
- **FR-140-014**: System MUST prevent accepting revoked, expired, replaced, or already accepted invitations.
- **FR-140-015**: System MUST support Owner/Admin revocation and resend of pending invitations.
- **FR-140-016**: System MUST NOT implement non-enterprise workspace invitation branching.
- **FR-140-017**: System MUST treat `membershipType` in the invite request as the inviter's explicit choice and MUST persist that choice on the invitation; it MUST NOT infer or overwrite it from the email domain.
- **FR-140-018**: System MUST fall back to a domain-derived suggestion for `membershipType` only when the request omits it, so existing API clients keep working.
- **FR-140-019**: System MUST skip every invitee-domain check — verified-domain matching and public-domain rejection alike — when `RequireVerifiedDomainForInternal = false`.
- **FR-140-020**: System MUST reject an `Internal` invitation to a public email domain when `RequireVerifiedDomainForInternal = true`, with an error distinct from the generic unverified-domain error.
- **FR-140-021**: System MUST validate invitation policy twice — once at creation against the settings in force then, and again at acceptance against the settings in force at that moment — and MUST treat the acceptance-time result as authoritative.
- **FR-140-022**: System MUST honour `AllowSubdomains` identically in the creation check and the acceptance check.
- **FR-140-023**: System MUST reject acceptance of an invitation whose stored `membershipType` has become illegal under current workspace settings, with an error naming the setting that changed.
- **FR-140-025**: System MUST apply one shared policy-validation routine to both invitation creation and invitation acceptance, including the `External ⇒ role Member` constraint. Acceptance MUST NOT admit a membership that creation would have refused.
- **FR-140-024**: System MUST expose the workspace's current invitation policy (`requireVerifiedDomainForInternal`, `allowExternalCollaboration`, `allowSubdomains`, verified domains) to the invite form so the client can pre-select and disable options without guessing.

---

## 6. Business Rules

- **BR-140-001**: Workspace invitation applies only to Enterprise Workspaces.
- **BR-140-002**: Owner/Admin can invite; Member and External Member cannot invite.
- **BR-140-003**: Admin cannot assign Owner through invitation.
- **BR-140-004**: External collaborator must be `MembershipType = External` and role `Member`.
- **BR-140-005**: `RequireVerifiedDomainForInternal` is the single switch that turns invitee-domain validation on. When it is `true`, an `Internal` invitee's email domain must match a verified domain of this workspace. When it is `false`, the invitee's domain is not validated at all and any address may be invited as `Internal`.
- **BR-140-006**: Public email domains must not be accepted as enterprise verified domains, and — only while `RequireVerifiedDomainForInternal = true` — must not be invited as `Internal`. With the flag `false` a public-domain address is an ordinary invitee like any other.
- **BR-140-007**: A user cannot be an active Internal member of more than one Enterprise Workspace when verified-domain enforcement applies.
- **BR-140-008**: External members may belong to multiple Enterprise Workspaces, subject to each workspace's invitation policy.
- **BR-140-009**: A resend replaces the old pending token; only the newest pending token is acceptable.
- **BR-140-010**: Accepting an invitation is identity-bound; possession of a token is not sufficient without exact email match.
- **BR-140-011**: Membership type is chosen by the inviter, not inferred by the system. The domain policy only decides which choices are *legal*; it never decides *for* the inviter.
- **BR-140-012**: Domain validation runs twice. Creation-time validation exists to fail fast for the inviter; acceptance-time validation is the authoritative gate, because membership is created there and workspace settings may have changed in between.
- **BR-140-013**: An invitation carries the inviter's intent, not a policy snapshot. Acceptance re-evaluates that intent against the settings in force at that moment and has exactly two outcomes: admit it unchanged, or **refuse it**. Refusal is the only correct fallback — admitting an invitation that current settings would not permit lets a stale token override live workspace policy, which is precisely what the policy exists to prevent. Downgrading `Internal` to `External` (or the reverse) to force a pass is equally forbidden: it hands the invitee an access class nobody approved.
- **BR-140-014**: Tightening a policy invalidates pending invitations that the new policy would have refused. Those invitations stay `PENDING` and fail at acceptance with the reason; Owner/Admin must revoke or re-issue them.
- **BR-140-015**: Loosening a policy never retroactively upgrades a pending invitation. Someone invited as `External` stays `External` even if their domain is verified afterwards; changing their access class is a separate, deliberate action.
- **BR-140-016**: `AllowSubdomains` applies uniformly wherever a domain is matched — creation, acceptance, join requests. A subdomain accepted at invite time must not be refused at accept time.

### 6.1. Verified-Domain Enforcement Matrix

`RequireVerifiedDomainForInternal` (viết tắt: `RequireVD`) là công tắc duy nhất. Bảng dưới là hành vi bắt buộc tại **cả hai** thời điểm create và accept:

| `RequireVD` | Inviter chọn | Domain invitee | Kết quả |
|---|---|---|---|
| `false` | `Internal` | bất kỳ (kể cả `gmail.com`) | Cho phép. Không validate domain. |
| `false` | `External` | bất kỳ | Cho phép nếu `AllowExternalCollaboration = true`, role bắt buộc `Member`. |
| `true` | `Internal` | khớp verified domain (hoặc subdomain khi `AllowSubdomains = true`) | Cho phép. |
| `true` | `Internal` | không khớp verified domain | Từ chối — `CannotInviteInternalWithoutVerifiedDomain`. |
| `true` | `Internal` | public domain | Từ chối — lỗi riêng, nói rõ public domain không thể là enterprise domain. |
| `true` | `External` | bất kỳ | Cho phép nếu `AllowExternalCollaboration = true`, role bắt buộc `Member`. |

Hai hệ quả cần giữ đúng:

- Khi `RequireVD = false`, workspace không phân biệt nội bộ/bên ngoài theo tên miền. `External` vẫn là lựa chọn hợp lệ vì nó quyết định quyền, không phải quyết định tên miền.
- Public-domain check chỉ là trường hợp riêng của verified-domain check. Nó không phải một luật độc lập, và không được chạy khi `RequireVD = false`.

### 6.2. Policy Changes While an Invitation Is Pending

Invitation sống tối đa `InvitationExpiryDays`. Trong khoảng đó Owner có thể đổi settings. Quy tắc xử lý:

| Thay đổi giữa create và accept | Invitation `Internal` đang pending | Invitation `External` đang pending |
|---|---|---|
| `RequireVD` `false` → `true`, domain khớp | Accept được | Không ảnh hưởng |
| `RequireVD` `false` → `true`, domain không khớp | Từ chối tại accept, nêu rõ setting đã đổi | Không ảnh hưởng |
| `RequireVD` `true` → `false` | Accept được | Không ảnh hưởng |
| Verified domain bị gỡ, đúng domain của invitee | Từ chối tại accept | Không ảnh hưởng |
| Verified domain được thêm, khớp domain invitee | Không ảnh hưởng | Vẫn `External` (BR-140-015) |
| `AllowExternalCollaboration` `true` → `false` | Không ảnh hưởng | Từ chối tại accept |
| `AllowSubdomains` `true` → `false`, invitee ở subdomain | Từ chối tại accept | Không ảnh hưởng |

Bắt buộc:

- Invitation bị chặn **không** tự chuyển sang `EXPIRED` hay `REVOKED`. Nó vẫn `PENDING` và fail ở accept với lý do cụ thể, để Owner/Admin còn thấy nó trong danh sách và quyết định revoke hay re-issue.
- Màn hình workspace settings phải cảnh báo trước khi lưu một thay đổi làm hỏng invitation đang pending: số lượng invitation bị ảnh hưởng và email của họ.
- Thông báo lỗi tại accept phải nêu setting nào đã đổi, không dùng lỗi validation chung chung — người nhận không có cách nào tự chẩn đoán.

### 6.3. User Stories by Business Rule (Linear WT-140 Aligned)

Nguồn: Linear WT-140 yêu cầu owner/admin invitation path và invited-user acceptance path phải verify được; trạng thái pending, accepted, revoked, expired, invalid và duplicate invitation phải được định nghĩa; token security/storage rule phải rõ.

| Business Rule | User story | Acceptance scenarios |
|---|---|---|
| BR-140-001 | Là Owner/Admin trong B2B Enterprise Workspace, tôi muốn mọi invitation thuộc đúng workspace doanh nghiệp để membership không bị tạo ở personal/non-enterprise context. | Given invitation request có workspaceId hợp lệ, When request được xử lý, Then invitation được scope vào Enterprise Workspace; Given flow cố tạo non-enterprise invitation, Then hệ thống reject hoặc không expose flow. |
| BR-140-002 | Là Owner/Admin, tôi muốn chỉ vai trò quản trị được mời thành viên để kiểm soát quyền truy cập tổ chức. | Given caller là Owner/Admin, When invite hợp lệ, Then tạo PENDING invitation; Given caller là Member/External Member, When invite, Then trả 403. |
| BR-140-003 | Là Owner, tôi muốn Admin không thể cấp Owner qua invite để tránh leo thang quyền sở hữu. | Given Admin tạo invite với role Owner, When validate role, Then reject; Given Owner muốn chuyển quyền sở hữu, Then dùng ownership transfer flow riêng, không dùng invitation. |
| BR-140-004 | Là enterprise manager, tôi muốn outside-domain collaborator luôn là External Member role Member để họ không quản trị dữ liệu nội bộ. | Given external email và role Member, When external collaboration enabled, Then tạo external pending invite; Given role Owner/Admin, Then reject hoặc force Member theo policy đã phê duyệt. |
| BR-140-005 | Là workspace owner, tôi muốn internal invite phải khớp verified domain khi workspace yêu cầu, và không bị chặn vì tên miền khi workspace không yêu cầu. | Given `RequireVerifiedDomainForInternal=true`, When email domain thuộc verified domains, Then accept request; When domain không khớp, Then reject validation; Given `RequireVerifiedDomainForInternal=false`, When invite bất kỳ email nào là Internal, Then không chạy domain validation và tạo invitation. |
| BR-140-006 | Là enterprise owner, tôi muốn public email domain không được dùng làm verified enterprise domain để tránh giả danh tổ chức. | Given domain như gmail.com/yahoo.com, When dùng cho internal verification hoặc invite Internal lúc `RequireVerifiedDomainForInternal=true`, Then reject với lỗi riêng cho public domain; Given `RequireVerifiedDomainForInternal=false`, Then public domain được đối xử như mọi domain khác. |
| BR-140-007 | Là hệ thống B2B, tôi muốn một Internal Member chỉ thuộc một Enterprise Workspace khi domain enforcement bật để tránh trộn dữ liệu tổ chức. | Given user đã là Internal member ở workspace khác, When accept internal invite vào workspace mới, Then reject; Given user là External Member, Then rule này không chặn nhiều workspace. |
| BR-140-008 | Là external collaborator, tôi muốn có thể tham gia nhiều enterprise workspace theo từng lời mời riêng để hỗ trợ vendor/partner work. | Given external collaboration enabled ở nhiều workspaces, When user accept từng invite hợp lệ, Then tạo membership riêng ở mỗi workspace; Given workspace disabled external collaboration, Then reject. |
| BR-140-009 | Là Owner/Admin, tôi muốn resend thay thế token cũ để token stale không còn dùng được. | Given pending invite tồn tại, When resend cùng email, Then old invite chuyển REPLACED và token mới là token duy nhất accept được. |
| BR-140-010 | Là invited user, tôi muốn invitation chỉ accept được bằng đúng tài khoản email được mời để token leak không tạo truy cập sai người. | Given token hợp lệ nhưng authenticated email khác invited email, When accept, Then reject; Given email khớp chính xác, Then invitation ACCEPTED và membership active được tạo/reactivate. |
| BR-140-011 | Là Owner/Admin, tôi muốn tự chọn Internal hay External khi mời để quyền truy cập là quyết định của tôi, không phải suy đoán từ tên miền. | Given invite form, When nhập email, Then dropdown pre-select theo domain policy nhưng vẫn đổi được; Given inviter chọn External cho một email thuộc verified domain, Then invitation lưu External. |
| BR-140-012 | Là hệ thống, tôi muốn validate domain cả lúc tạo lẫn lúc accept để inviter biết lỗi ngay còn membership vẫn được gác đúng ở thời điểm tạo ra. | Given invite vi phạm policy, When create, Then reject ngay không gửi email; Given invite hợp lệ lúc create nhưng vi phạm lúc accept, Then accept bị từ chối. |
| BR-140-013 | Là invited user, tôi muốn được join đúng access class đã ghi trong lời mời để không bị đổi quyền âm thầm giữa chừng. | Given invitation lưu Internal và vẫn hợp lệ, When accept, Then membership là Internal; Given intent đã thành bất hợp lệ, Then reject chứ không tự đổi sang External. |
| BR-140-014 | Là Owner, tôi muốn siết policy làm vô hiệu các invitation pending vi phạm để policy mới có hiệu lực thật. | Given pending invite Internal domain chưa verified, When bật `RequireVerifiedDomainForInternal`, Then invite giữ PENDING nhưng accept bị từ chối kèm lý do; Given settings screen, When lưu thay đổi, Then cảnh báo số invitation bị ảnh hưởng. |
| BR-140-015 | Là enterprise manager, tôi muốn nới policy không tự nâng cấp invitation cũ để việc lên Internal luôn là hành động có chủ đích. | Given pending invite External, When domain của họ được verify, Then accept vẫn tạo External membership. |
| BR-140-016 | Là workspace owner bật subdomain, tôi muốn subdomain được đối xử nhất quán để invitation không chết giữa create và accept. | Given `AllowSubdomains=true` và verified `company.com`, When invite `a@eng.company.com` là Internal, Then create thành công và accept cũng thành công. |

---

## 7. Non-functional Requirements

- **NFR-140-001**: Invitation token comparison MUST use secure hash lookup and must not log plaintext token values.
- **NFR-140-002**: Invitation preview MUST avoid exposing private member directory, document, meeting, billing, or settings data.
- **NFR-140-003**: Invitation list endpoints MUST be paginated and scoped by workspace authorization.
- **NFR-140-004**: Invitation acceptance MUST be idempotency-safe under concurrent accept attempts.
- **NFR-140-005**: Invitation email dispatch failure MUST not create an accepted membership; pending invitation retry/resend must remain possible.

---

## 8. Known Implementation Gaps (as of 2026-08-10)

Ghi lại khoảng cách giữa spec này và code hiện tại, để phần implement biết chính xác phải sửa gì.

1. **`membershipType` trong request bị bỏ qua.** `InviteMemberRequest.MembershipType` tồn tại nhưng `WorkspaceInvitationService.InviteMemberAsync` không đọc; nó gọi `WorkspaceHelper.DetermineMembershipTypeAsync` và tự suy ra từ domain. Trường này hiện chỉ được dùng ở luồng approve join request. → FR-140-017, FR-140-018.
2. **Không có public-domain check ở luồng invite.** `EmailAddress.IsPublicDomainName` chỉ được gọi khi tạo workspace và khi thêm/sửa verified domain. Một email `@gmail.com` hiện không bị chặn ở invite dưới bất kỳ setting nào. → FR-140-020.
3. **`RequireVerifiedDomainForInternal = false` phân loại mọi người thành Internal.** `WorkspaceHelper.ResolveMembershipType` trả `Internal` ngay khi `requireVerifiedDomain = false`, nên với cờ tắt thì `External` là trạng thái không thể đạt tới qua invite, và `AllowExternalCollaboration` trở thành cờ chết. Dropdown ở FR-140-017 là cách sửa.
4. **Accept-time domain check bỏ qua `AllowSubdomains`.** `WorkspaceInvitationAcceptanceProcessor.ValidateAcceptanceAsync` so khớp domain bằng truy vấn bằng-đúng-chuỗi, trong khi `DetermineMembershipTypeAsync` có tính subdomain. Với `AllowSubdomains = true`, verified `company.com`, invitee `a@eng.company.com`: create thành công, accept bị từ chối `CannotInviteInternalWithoutVerifiedDomain` — invitation không thể dùng được và không có cách khắc phục. → FR-140-022, BR-140-016.
5. **Accept ghi đè `invitation.MembershipType`, và không validate lại role.** `ProcessAcceptanceAsync` tính lại membership type theo settings tại thời điểm accept rồi gán đè lên giá trị đã lưu (dòng 159) — đây là "âm thầm phân loại lại" mà BR-140-013 cấm. Nặng hơn: dòng 160–164 truyền thẳng `invitation.RoleId` vào member mới, và accept path không có check nào tương ứng với `ExternalMemberMustHaveMemberRole` ở create path. Một invitation `Internal` + role `Admin`, được accept sau khi verified domain bị gỡ, sẽ bị hạ thành `External` nhưng **giữ nguyên role Admin** — tạo ra External Member quản trị được workspace, trái BR-140-004 và FR-140-006. → FR-140-025.
6. **Hai nguồn sự thật cho verified domains.** Các check domain đọc bảng `workspace_verified_domains`, còn `WorkspaceConfiguration.VerifiedDomains` trong settings JSON vẫn được dùng ở vài nhánh (`IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync`, nhánh `isEnterpriseWorkspace` khi accept). Comment WT-179 trong `WorkspaceInvitationAcceptanceProcessor` mô tả một sự cố production do đúng chỗ lệch này. Cần chốt bảng là nguồn duy nhất.

---

## 9. Out of Scope

- Non-enterprise workspace invitation flows.
- Any separate workspace type outside Enterprise Workspace.
- Automatic conversion between workspace types.
- Billing/subscription approval for invited members.
- Document or meeting access grants beyond the membership created by accepting the invitation.
