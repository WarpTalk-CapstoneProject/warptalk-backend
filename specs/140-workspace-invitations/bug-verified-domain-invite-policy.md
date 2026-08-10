# Linear ticket draft — paste vào Linear

**Title**: `[Workspace] Verified-domain invite: accept-time tự hạ access class và giữ role, bỏ qua AllowSubdomains, không có public-domain check`

**Label**: `bug`
**Assignee**: (bạn)
**Team / Project**: Workspace
**Priority**: Urgent
**Related**: WT-140, WT-157, WT-179

> Priority đặt Urgent vì Bug 4 tạo được External Member giữ role Admin — vượt qua đúng ràng buộc mà create path đang enforce. Ba bug còn lại là High.

---

## Summary

Luồng verified domain của invitation lệch với business rule ở bốn điểm. Một điểm tạo ra invitation không thể accept được và không có cách khắc phục từ phía người dùng (Bug 1). Một điểm cho phép tạo External Member giữ role Admin (Bug 4).

Cả bốn đều xoay quanh cùng một nguyên nhân gốc: **membership type đang được hệ thống suy ra từ email domain thay vì do inviter quyết định**, và giá trị suy ra đó được tính lại một lần nữa lúc accept — bằng một bộ luật viết rời, không khớp với bộ luật ở create path.

---

## Bug 1 — Invitation subdomain tạo được nhưng không accept được (blocker)

**Nghiêm trọng nhất. Không có workaround.**

Create-time và accept-time so khớp domain bằng hai logic khác nhau:

- Create: `WorkspaceHelper.DetermineMembershipTypeAsync` → `ResolveMembershipType`, **có** tính `AllowSubdomains`.
- Accept: `WorkspaceInvitationAcceptanceProcessor.ValidateAcceptanceAsync` truy vấn `vd.Domain.ToLower() == userDomain.ToLower()`, **bỏ qua** `AllowSubdomains`.

### Repro

1. Workspace bật `RequireVerifiedDomainForInternal = true` và `AllowSubdomains = true`.
2. Verified domain: `company.com`.
3. Owner mời `a@eng.company.com`.
4. Create thành công → invitation `PENDING`, email được gửi.
5. Invitee đăng nhập đúng email đó và accept.

**Expected**: accept thành công, tạo Internal membership.
**Actual**: `CannotInviteInternalWithoutVerifiedDomain`. Invitation vĩnh viễn không dùng được. Owner không thấy lý do, invitee không tự chẩn đoán được.

### Fix

Dùng `WorkspaceHelper.IsEmailDomainVerifiedAsync` ở cả hai nơi — nó đã xử lý `AllowSubdomains` đúng. Xoá truy vấn `AnyAsync` inline ở accept path.

---

## Bug 2 — `membershipType` do client gửi bị bỏ qua hoàn toàn

`InviteMemberRequest.MembershipType` đã tồn tại trong DTO nhưng `WorkspaceInvitationService.InviteMemberAsync` không đọc nó; service tự gọi `DetermineMembershipTypeAsync`. Trường này hiện chỉ được dùng ở luồng approve join request.

Hệ quả:

- Inviter không thể chủ động mời ai đó làm External nếu domain của họ tình cờ đã verified.
- Khi `RequireVerifiedDomainForInternal = false`, `ResolveMembershipType` trả `Internal` cho **mọi** email → External là trạng thái không thể đạt tới qua invite, và `AllowExternalCollaboration` trở thành cờ chết ở luồng này.
- Invite form đang nói thẳng với người dùng rằng đây là hành vi cố ý: "Internal or External access is assigned automatically from the workspace's verified domains" (`warptalk-web/src/components/workspace/invite-member-dialog.tsx`).

### Fix

- BE: đọc `request.MembershipType` làm lựa chọn có thẩm quyền; chỉ suy ra từ domain khi request bỏ trống (giữ backward-compat).
- FE: thêm dropdown Access type (Internal / External) vào invite form, pre-select theo domain policy nhưng cho phép đổi. Disable option `External` kèm lý do khi `AllowExternalCollaboration = false`. Chọn `External` thì ép role `Member`.
- Cần endpoint/field trả policy hiện tại của workspace cho form (`requireVerifiedDomainForInternal`, `allowExternalCollaboration`, `allowSubdomains`, verified domains) để FE không phải đoán.

---

## Bug 3 — Không có public-domain check nào ở luồng invite

`EmailAddress.IsPublicDomainName` chỉ được gọi khi tạo workspace (`WorkspaceService.cs`) và khi thêm/sửa verified domain (`VerifiedDomainService.cs`). Luồng invite không gọi ở đâu cả.

Hiện tại `@gmail.com` không bị chặn dưới bất kỳ setting nào — với `RequireVerifiedDomainForInternal = true` nó chỉ bị xếp lặng lẽ thành External.

### Fix

Khi `RequireVerifiedDomainForInternal = true` và inviter chọn `Internal`, reject public domain bằng một error code **riêng**, không dùng chung lỗi unverified-domain — hai trường hợp này cần hai hướng dẫn khắc phục khác nhau (một cái "verify domain đi", một cái "public domain không bao giờ verify được").

Khi cờ = `false`, **không** chạy check này. Public-domain check là trường hợp riêng của verified-domain check, không phải luật độc lập.

---

## Bug 4 — Accept tự hạ Internal → External để ép pass, và giữ nguyên role (privilege leak)

`ProcessAcceptanceAsync` tính lại membership type theo settings tại thời điểm accept rồi **ghi đè** lên `invitation.MembershipType` đã lưu (dòng 159). Đây không chỉ là "bỏ qua lựa chọn của inviter" — nó là một đường lách: thay vì từ chối một invitation đã xung đột với policy hiện tại, hệ thống tự hạ access class xuống để cho qua.

Nghiêm trọng hơn: **role không được validate lại sau khi membership type bị ghi đè.** Dòng 160–164 truyền thẳng `invitation.RoleId` vào `CreateInvitationMember`, và không có check nào ở accept path tương ứng với `ExternalMemberMustHaveMemberRole` mà create path đang enforce.

### Repro

1. Workspace: `RequireVerifiedDomainForInternal = true`, verified domain `company.com`, `AllowExternalCollaboration = true`.
2. Owner mời `a@company.com` với role **Admin** → invitation lưu `MembershipType = Internal`, `RoleName = Admin`. Hợp lệ tại thời điểm này.
3. Trước khi invitee accept, Owner gỡ `company.com` khỏi verified domains (hoặc domain bị revoke).
4. Invitee accept.

**Expected**: từ chối. Invitation được duyệt dựa trên một domain nay không còn verified.
**Actual**: accept **thành công**. `DetermineMembershipTypeAsync` trả `External`, dòng 159 ghi đè, `AllowExternalCollaboration = true` nên check ở dòng 148 cho qua, và member được tạo với `MembershipType = External` + **role Admin**.

Kết quả là một External Member giữ role Admin — đúng thứ mà BR-140-004 và FR-140-006 cấm, và đúng thứ mà create path chặn được nhưng accept path thì không. Người này quản trị được workspace mà không ai từng phê duyệt họ ở access class đó.

### Fix

- Bỏ hoàn toàn việc ghi đè ở dòng 159. Membership type là giá trị đã lưu, không phải giá trị tính lại.
- Accept path phải chạy **cùng bộ luật** với create path, gồm cả `External ⇒ role Member`. Tách phần validate thành một hàm dùng chung cho cả hai đường, thay vì hai bản luật viết rời như hiện nay — chính chỗ viết rời này sinh ra cả Bug 1 lẫn Bug 4.
- Khi intent đã lưu không còn hợp lệ → **từ chối**, nêu rõ setting nào đã đổi.

---

## Nguyên tắc chốt — accept-time phải từ chối, không được tự sửa

Nguyên tắc đứng sau Bug 4, và là thứ phần fix phải bám vào:

> Invitation lưu **ý định của inviter**, không lưu snapshot policy. Lúc accept, re-check ý định đó với settings hiện tại. Chỉ có hai kết quả: cho qua nguyên vẹn, hoặc **từ chối**.

Từ chối là fallback duy nhất đúng. Nếu cho pass, một token cũ sẽ ghi đè policy đang có hiệu lực của workspace — đúng thứ mà policy sinh ra để ngăn. Tự hạ `Internal` → `External` để ép pass cũng bị cấm: nó trao cho invitee một access class không ai phê duyệt.

Kèm theo:

- Invitation bị chặn **giữ nguyên `PENDING`**, không tự chuyển `EXPIRED`/`REVOKED`, để Owner/Admin còn thấy trong danh sách và quyết định revoke hay re-issue.
- Error lúc accept phải nêu **setting nào đã đổi**, không dùng validation error chung chung.
- Màn hình workspace settings phải cảnh báo trước khi lưu thay đổi làm hỏng invitation pending: số lượng và email bị ảnh hưởng.
- Nới policy **không** nâng cấp ngược. Ai được mời External thì vẫn External kể cả khi domain của họ được verify sau đó.

---

## Spec cần chỉnh sửa

`warptalk-backend/specs/140-workspace-invitations/spec.md` đã được cập nhật (BR-140-005/006 viết lại, BR-140-011 → BR-140-016 mới, FR-140-017 → FR-140-024, User Story 5, ma trận §6.1, bảng policy-change §6.2, gap list §8). Các spec dưới đây **mâu thuẫn trực tiếp** với BR mới và phải sửa theo:

| Spec | Chỗ mâu thuẫn | Cần sửa thành |
|---|---|---|
| `specs/workspace-module-requirements/workspace-module-overview.md` — FR-WS-013 (dòng ~515) | "Email ngoài verified domain chỉ được mời khi AllowExternalCollaboration=true và **bị ép** role External Member" | Membership type do inviter chọn (BR-140-011). Domain policy chỉ quyết định lựa chọn nào **hợp lệ**, không quyết định thay inviter. |
| `specs/workspace-module-requirements/workspace-module-overview.md` — mermaid flow (dòng ~390) | Node `"Force External Member"` | Đổi thành nhánh validate lựa chọn của inviter, không phải nhánh ép kiểu. |
| `docs/external-member-workspace-permission-plan.md` — mục 2, 65–67, 106 | Định nghĩa `ResolveMembershipType(email, verifiedDomains, requireVerifiedDomainForInternal)` là **policy gán** membership type: match → internal, không match → external | Hạ xuống thành hàm **gợi ý** cho FE pre-select. Bổ sung nhánh `requireVerifiedDomainForInternal = false` → không validate domain (BR-140-005). |
| `docs/external-member-workspace-permission-plan.md` — mục 14 | Migration "can chinh membership type theo verified domains" cho workspace có cờ bật | Mâu thuẫn BR-140-013/015 — migration này sẽ viết đè lựa chọn của inviter và nâng cấp ngược member cũ. Cần giới hạn phạm vi hoặc bỏ. |
| `specs/139-workspace-creation-selection/workspace-types-and-role-permissions-acceptance-criteria.md` — dòng ~104 | Chỉ nêu mặt `true`: "Internal invitation requires a verified domain when RequireVerifiedDomainForInternal = true" | Bổ sung mặt `false` (không validate domain) và mệnh đề public-domain có điều kiện. |
| `specs/workspace-module-requirements/ui-screens/workspace-invitations.md` — dòng ~50, 59 | "Validate verified domain, role Admin/Member only" + domain policy hint | Thêm dropdown Access type vào invite form spec; nêu rõ pre-select, disable + lý do, ép role Member khi External. |
| `specs/workspace-module-requirements/ui-screens/workspace-settings-domains.md` | Không có gì về ảnh hưởng lên invitation đang pending | Thêm cảnh báo trước khi lưu khi thay đổi làm hỏng invitation pending (BR-140-014). |
| `specs/140-workspace-invitations/plan.md` — dòng ~118 | Task `MembershipType` viết theo contract cũ | Cập nhật theo FR-140-017/018. |
| `.agents/resources/sequence diagram/workspace/08-configure-verified-domains.puml` | Sequence không có bước re-validate lúc accept | Vẽ lại theo two-phase validation (FR-140-021). |

Ngoài ra, cần chốt **một nguồn sự thật cho verified domains**: các check domain đọc bảng `workspace.workspace_verified_domains`, nhưng `WorkspaceConfiguration.VerifiedDomains` trong settings JSON vẫn được dùng ở `IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync` và ở nhánh `isEnterpriseWorkspace` lúc accept. Comment WT-179 trong `WorkspaceInvitationAcceptanceProcessor` mô tả một sự cố production sinh ra từ đúng chỗ lệch này (workspace `testworkspace`: ba invitation pending, không cái nào accept được).

---

## Acceptance criteria

- [ ] `AllowSubdomains = true`, verified `company.com`, mời `a@eng.company.com` là Internal → create **và** accept đều thành công.
- [ ] Inviter chọn `External` cho một email thuộc verified domain → invitation lưu `External`, accept tạo External membership.
- [ ] `AllowExternalCollaboration = false` → option `External` bị disable kèm lý do; request `External` gửi thẳng lên server vẫn bị reject.
- [ ] `RequireVerifiedDomainForInternal = true` + mời `@gmail.com` là Internal → reject bằng error code riêng cho public domain.
- [ ] `RequireVerifiedDomainForInternal = false` + mời `@gmail.com` là Internal → thành công, không chạy domain validation nào.
- [ ] Invite Internal khi cờ tắt → bật cờ lên, domain không khớp → accept bị từ chối, invitation vẫn `PENDING`, error nêu rõ setting đã đổi.
- [ ] Invite External → domain được verify sau đó → accept vẫn tạo `External` membership.
- [ ] `ProcessAcceptanceAsync` không còn ghi đè `invitation.MembershipType`.
- [ ] Invite Internal + role Admin khi domain còn verified → gỡ verified domain → accept **bị từ chối**, không tạo member nào.
- [ ] Không tồn tại đường nào tạo được `WorkspaceMember` có `MembershipType = External` kèm role khác `Member`; có test phủ riêng cho accept path, không chỉ create path.
- [ ] Create path và accept path dùng chung một hàm validate policy, không phải hai bản luật viết rời.
- [ ] Settings screen cảnh báo số invitation pending sẽ hỏng trước khi lưu.

## Files chạm vào

- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Services/WorkspaceInvitationAcceptanceProcessor.cs`
- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Services/WorkspaceInvitationService.cs`
- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/Helpers/WorkspaceHelper.cs`
- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Domain/Constants/` (error code mới cho public domain)
- `warptalk-web/src/components/workspace/invite-member-dialog.tsx`
- Tests: `WorkspaceInvitationServiceTests.cs`, `Integration/WorkspaceInvitationIntegrationTests.cs`, `WorkspaceHelperTests.cs`
