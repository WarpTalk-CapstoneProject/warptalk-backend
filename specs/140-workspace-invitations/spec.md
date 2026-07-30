# Feature Specification: Enterprise Workspace Invitations (WT-140)

**Feature Branch**: `feat/auth`  
**Created**: 2026-05-24  
**Updated**: 2026-06-11  
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
  - `WorkspaceMember.MembershipType` (`Internal` or `External`)

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
3. **Given** an email domain is public or already verified by another workspace,  
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

---

## 6. Business Rules

- **BR-140-001**: Workspace invitation applies only to Enterprise Workspaces.
- **BR-140-002**: Owner/Admin can invite; Member and External Member cannot invite.
- **BR-140-003**: Admin cannot assign Owner through invitation.
- **BR-140-004**: External collaborator must be `MembershipType = External` and role `Member`.
- **BR-140-005**: Internal invitation must satisfy verified-domain rules when the workspace requires verified internal domains.
- **BR-140-006**: Public email domains must not be accepted as enterprise verified domains.
- **BR-140-007**: A user cannot be an active Internal member of more than one Enterprise Workspace when verified-domain enforcement applies.
- **BR-140-008**: External members may belong to multiple Enterprise Workspaces, subject to each workspace's invitation policy.
- **BR-140-009**: A resend replaces the old pending token; only the newest pending token is acceptable.
- **BR-140-010**: Accepting an invitation is identity-bound; possession of a token is not sufficient without exact email match.

### 6.1. User Stories by Business Rule (Linear WT-140 Aligned)

Nguồn: Linear WT-140 yêu cầu owner/admin invitation path và invited-user acceptance path phải verify được; trạng thái pending, accepted, revoked, expired, invalid và duplicate invitation phải được định nghĩa; token security/storage rule phải rõ.

| Business Rule | User story | Acceptance scenarios |
|---|---|---|
| BR-140-001 | Là Owner/Admin trong B2B Enterprise Workspace, tôi muốn mọi invitation thuộc đúng workspace doanh nghiệp để membership không bị tạo ở personal/non-enterprise context. | Given invitation request có workspaceId hợp lệ, When request được xử lý, Then invitation được scope vào Enterprise Workspace; Given flow cố tạo non-enterprise invitation, Then hệ thống reject hoặc không expose flow. |
| BR-140-002 | Là Owner/Admin, tôi muốn chỉ vai trò quản trị được mời thành viên để kiểm soát quyền truy cập tổ chức. | Given caller là Owner/Admin, When invite hợp lệ, Then tạo PENDING invitation; Given caller là Member/External Member, When invite, Then trả 403. |
| BR-140-003 | Là Owner, tôi muốn Admin không thể cấp Owner qua invite để tránh leo thang quyền sở hữu. | Given Admin tạo invite với role Owner, When validate role, Then reject; Given Owner muốn chuyển quyền sở hữu, Then dùng ownership transfer flow riêng, không dùng invitation. |
| BR-140-004 | Là enterprise manager, tôi muốn outside-domain collaborator luôn là External Member role Member để họ không quản trị dữ liệu nội bộ. | Given external email và role Member, When external collaboration enabled, Then tạo external pending invite; Given role Owner/Admin, Then reject hoặc force Member theo policy đã phê duyệt. |
| BR-140-005 | Là workspace owner, tôi muốn internal invite phải khớp verified domain khi workspace yêu cầu để bảo vệ tenant boundary. | Given `RequireVerifiedDomainForInternal=true`, When email domain thuộc verified domains, Then accept request; When domain không khớp, Then reject validation. |
| BR-140-006 | Là enterprise owner, tôi muốn public email domain không được dùng làm verified enterprise domain để tránh giả danh tổ chức. | Given domain như gmail.com/yahoo.com, When dùng cho internal verification, Then reject; Given domain công ty hợp lệ, Then tiếp tục validation ownership/uniqueness. |
| BR-140-007 | Là hệ thống B2B, tôi muốn một Internal Member chỉ thuộc một Enterprise Workspace khi domain enforcement bật để tránh trộn dữ liệu tổ chức. | Given user đã là Internal member ở workspace khác, When accept internal invite vào workspace mới, Then reject; Given user là External Member, Then rule này không chặn nhiều workspace. |
| BR-140-008 | Là external collaborator, tôi muốn có thể tham gia nhiều enterprise workspace theo từng lời mời riêng để hỗ trợ vendor/partner work. | Given external collaboration enabled ở nhiều workspaces, When user accept từng invite hợp lệ, Then tạo membership riêng ở mỗi workspace; Given workspace disabled external collaboration, Then reject. |
| BR-140-009 | Là Owner/Admin, tôi muốn resend thay thế token cũ để token stale không còn dùng được. | Given pending invite tồn tại, When resend cùng email, Then old invite chuyển REPLACED và token mới là token duy nhất accept được. |
| BR-140-010 | Là invited user, tôi muốn invitation chỉ accept được bằng đúng tài khoản email được mời để token leak không tạo truy cập sai người. | Given token hợp lệ nhưng authenticated email khác invited email, When accept, Then reject; Given email khớp chính xác, Then invitation ACCEPTED và membership active được tạo/reactivate. |

---

## 7. Non-functional Requirements

- **NFR-140-001**: Invitation token comparison MUST use secure hash lookup and must not log plaintext token values.
- **NFR-140-002**: Invitation preview MUST avoid exposing private member directory, document, meeting, billing, or settings data.
- **NFR-140-003**: Invitation list endpoints MUST be paginated and scoped by workspace authorization.
- **NFR-140-004**: Invitation acceptance MUST be idempotency-safe under concurrent accept attempts.
- **NFR-140-005**: Invitation email dispatch failure MUST not create an accepted membership; pending invitation retry/resend must remain possible.

---

## 8. Out of Scope

- Non-enterprise workspace invitation flows.
- Any separate workspace type outside Enterprise Workspace.
- Automatic conversion between workspace types.
- Billing/subscription approval for invited members.
- Document or meeting access grants beyond the membership created by accepting the invitation.
