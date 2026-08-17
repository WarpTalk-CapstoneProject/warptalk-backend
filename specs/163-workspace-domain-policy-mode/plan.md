# Plan — Membership assignment policy (domain-verified vs. manually-assigned)

- **Branch**: `fix/workspace-verified-domain-mode` (backend `1186d66`, web `8edb20f`, cùng tách từ `origin/development`)
- **Ngày**: 2026-08-13
- **Trạng thái**: chờ duyệt, chưa sửa code

---

## 0. Thuật ngữ

`specs/workspace-module-requirements/CONTEXT.md:7-9` chốt: **Enterprise Workspace là mô hình
workspace duy nhất**, và ghi rõ _Avoid: "Personal workspace", "workspace type"_. Spec 139 cũng
chốt không có cột `WorkspaceType` (`spec.md:34`).

Vì vậy tài liệu này **không** gọi workspace không dùng verified domain là "non-Enterprise" hay
"workspace nhỏ". Cả hai đều là Enterprise Workspace. Thứ khác nhau giữa chúng là **membership
assignment policy** — cách Owner/Admin gán `Internal` / `External` bị ràng buộc tới đâu:

| Tên gọi | `require_verified_domain_for_internal` | Ý nghĩa |
|---|---|---|
| **Domain-verified membership** | `true` | Owner/Admin chọn membership type, nhưng lựa chọn `Internal` **bị ràng buộc** bởi verified domain: chỉ email thuộc domain đã verify mới được gán Internal. |
| **Manually-assigned membership** | `false` | Owner/Admin chọn membership type **không ràng buộc domain**. Internal/External vẫn phân biệt đầy đủ và vẫn có ý nghĩa — chỉ là ranh giới do người quản trị vạch ra, không do public/private domain vạch ra. |

Điểm mấu chốt của tên gọi mới: sự khác nhau nằm ở **ràng buộc lên lựa chọn**, không nằm ở việc
"có hay không có khái niệm Internal/External". Cách gọi cũ ("non-Enterprise") hàm ý workspace hạng
hai và hàm ý bên đó không phân biệt Internal/External — cả hai đều sai.

Trong tài liệu này, khi cần nói ngắn: **domain-verified** và **manual**.

> **Quan trọng (D-10):** cột này là **giá trị dẫn xuất**, không phải cấu hình. Nó luôn bằng
> "workspace có ≥1 verified domain đang active hay không". Owner không bật/tắt nó; Owner thêm hoặc
> revoke domain, và policy đi theo. Xem §4.7.

---

## 1. Problem statement

WarpTalk có hai membership policy ở tầng domain nhưng chỉ có một đường tạo ra chúng.

`WorkspaceHelper.ResolveMembershipType` phân nhánh trên `requireVerifiedDomain`
(`Helpers/WorkspaceHelper.cs:231`), `WorkspaceInvitationPolicy.ValidateAsync` cũng vậy
(`Helpers/WorkspaceInvitationPolicy.cs:128`). Nghĩa là policy `manual` đã tồn tại và đã được cài
đặt ở tầng domain.

Nhưng:

- form create không có control nào để chọn policy,
- backend chặn public email domain trước cả khi biết đang tạo policy nào,
- **cột lưu policy không được ghi lúc create**, nên policy thật của workspace không liên quan gì
  đến thứ caller yêu cầu,
- và ở nhiều đường, hệ thống vẫn **tự suy ra** membership type từ domain thay vì để Owner/Admin
  quyết định.

Điểm thứ ba nghiêm trọng nhất và đi ngược với triệu chứng: FE luôn yêu cầu domain-verified, nhưng
cột luôn nhận `false`. Kết quả là **mọi workspace tạo qua sản phẩm đều đang chạy ở policy manual**,
dù đã claim domain thành công và dù màn Settings có hiện chip domain. Mismatch mà tester nhìn thấy
chỉ là biểu hiện bề mặt. Chi tiết và bằng chứng ở RC-1.

---

## 2. Root cause

### RC-1 — Cột policy không được ghi lúc create *(bug authorization, mức production)*

`WorkspaceMapper.ToEntity` (`Mappers/Workspace/WorkspaceMapper.cs:36-51`) không gán
`RequireVerifiedDomainForInternal` lẫn `AllowExternalCollaboration`. Trong khi
`WorkspaceHelper.GetWorkspaceConfig` (`Helpers/WorkspaceHelper.cs:41-42`) và
`WorkspaceRepository.GetSettingsAsync` (`Repositories/WorkspaceRepository.cs:175-176`) lấy hai
**cột** đè lên JSON và coi cột là nguồn sự thật.

Hai cột đó vì vậy luôn nhận CLR default `false`, bất kể caller yêu cầu gì.

> **Đã kiểm chứng bằng thực nghiệm** (probe test trên PostgreSQL 16 thật, EF Core 10.0.5, đã xoá
> sau khi đọc kết quả):
>
> ```
> DDL  allow_external_collaboration          default=      nullable=NO
> DDL  require_verified_domain_for_internal  default=true  nullable=NO
> ROW  explicit-false   require=False  external=False
> ROW  explicit-true    require=True   external=True
> ROW  unset            require=False  external=False   ← hàng ToEntity sinh ra hôm nay
> ```
>
> Giả thuyết ban đầu — rằng `HasDefaultValue(true)` ở `Persistence/WorkspaceDbContext.cs:78-80`
> khiến EF bỏ cột khỏi INSERT và DB default `true` thắng — **sai với EF Core 10**. Từ EF Core 7,
> `HasDefaultValue(x)` đồng thời đặt *sentinel* của property thành `x`, nên `false` không còn bị
> coi là "chưa gán" và vẫn được ghi. Hành vi bỏ-cột đó chỉ đúng với EF Core 6 trở về trước.
> **Hệ quả: không cần sửa gì trong `WorkspaceDbContext`** (xem D-6).

Hai hệ quả độc lập:

1. FE **luôn** gửi `requireVerifiedDomainForInternal: true`
   (`web/src/app/(app)/workspace/create/page.tsx:134`). `CreateWorkspaceAsync` tính đúng
   `requireVerified = true`, ghi `true` vào settings JSON, và tạo row trong
   `workspace_verified_domains` (`Services/WorkspaceService.cs:187-194`). Nhưng **cột vẫn là
   `false`**, và cột mới là policy.

   ⇒ **Mọi workspace tạo qua sản phẩm đang chạy ở policy manual**, dù đã claim domain thành công.
   Cụ thể:
   - `DetermineMembershipTypeAsync` short-circuit ở `Helpers/WorkspaceHelper.cs:106` → mọi email
     thành `Internal`.
   - `WorkspaceInvitationPolicy.ValidateAsync` bỏ qua ràng buộc domain
     (`Helpers/WorkspaceInvitationPolicy.cs:128-134`) → mời gmail làm Internal vẫn lọt, trong khi
     Owner tin rằng workspace đang ràng buộc theo domain.
   - `IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync` không đếm workspace đó → rule "một
     internal home cho mỗi user" không được áp.
   - Toggle `Require Verified Domain` trong Settings hiển thị **OFF** trên workspace mà Owner tin
     là domain-verified — đúng triệu chứng tester báo.

   Đây là lỗ hổng authorization: policy Owner chọn không phải policy đang chạy.

2. `allow_external_collaboration` không khai default trong EF, DB default `false`
   (`warptalk-infrastructure/scripts/init-db.sql:187`). ⇒ **mọi workspace tạo qua API đều cấm
   external collaboration**, trong khi JSON vừa ghi nói `true`
   (`WorkspaceConfiguration.AllowExternalCollaboration` mặc định `true`). Sai lệch chỉ biến mất
   khi Owner vào Settings đụng vào toggle, vì `UpdateSettingsAsync` có ghi cột
   (`Repositories/WorkspaceRepository.cs:189-190`).

Workspace `WarpTalk Demo — SEP490` trong ảnh chụp không đi qua đường này vì nó được seed thẳng
bằng SQL với giá trị tường minh (`scripts/seed-prod-demo-accounts-workspace.sql:54`) — đó là lý do
workspace seed và workspace tạo thật hành xử khác nhau.

### RC-2 — Hai nguồn sự thật cho verified domains

`WorkspaceSettingsValidator.Validate` bắt buộc `settings.VerifiedDomains` non-empty khi bật
`RequireVerifiedDomainForInternal` (`Validators/WorkspaceSettingsValidator.cs:46-51`), và list đó
đọc từ **settings JSON**. Nhưng:

- UI hiển thị domain từ **bảng** `workspace.workspace_verified_domains`
  (`useVerifiedDomains`, `web/src/app/(app)/[workspaceSlug]/settings/page.tsx:177`),
- `VerifiedDomainService.AddDomainAsync` chỉ ghi bảng, **không sync JSON**
  (`Services/VerifiedDomainService.cs:106-109`),
- `RevokeDomainAsync` cũng chỉ soft-revoke trong bảng (`:232`).

⇒ Owner add domain qua UI, chip hiện lên, bật toggle → PATCH fail `VerifiedDomainsRequired`, và
autosave chỉ bắn toast đỏ không giải thích được. Chiều ngược lại nguy hiểm hơn: JSON còn domain đã
revoke thì validator vẫn cho bật. Đây đúng loại lỗi WT-179 mà `GetWorkspaceConfig` được viết để
chống — chỉ là validator chưa được đưa vào cùng nguyên tắc.

### RC-3 — Rule public-domain gắn nhầm vào caller thay vì vào policy

`Services/WorkspaceService.cs:97` chặn mọi caller có public email domain, không phụ thuộc policy;
FE chặn song song ở `getAccountIssue` (`web/src/app/(app)/workspace/create/page.tsx:272`).

Comment tại chỗ giải thích đây là rule về **caller** nên cố ý đặt ngoài flag, để
`{"requireVerifiedDomainForInternal": false}` không tắt được nó (WT-142). Lập luận đó đúng với
policy domain-verified: ở đó việc claim domain cấp tier Internal cho mọi người cùng domain, nên là
quyết định authorization. Với policy manual thì không có domain nào được claim và không có tier
nào được cấp theo domain — chặn ở đây là chặn nhầm.

Hai rule cần tách bạch:

- *"Không được claim public domain làm verified domain"* → giữ nguyên, vô điều kiện
  (`Services/WorkspaceService.cs:153-156`, `Services/VerifiedDomainService.cs:64-65`, và
  `specs/139-.../workspace-types-and-role-permissions-acceptance-criteria.md:27`).
- *"Public domain không được tạo workspace"* → chỉ áp với policy domain-verified.

### RC-4 — Hệ thống tự suy ra membership type thay vì để Owner/Admin quyết định

Ba chỗ vi phạm nguyên tắc "Owner/Admin quyết định" (D-7):

1. **Invite form không gửi `membershipType`.** Modal ghi *"Internal or External access is assigned
   automatically from the workspace's verified domains"*
   (`web/src/components/workspace/invite-member-dialog.tsx:232-235`) và không có field nào cho
   Access type. Backend vì thế rơi vào fallback inference
   (`Services/WorkspaceInvitationService.cs:104-111` gọi `DetermineMembershipTypeAsync`). Chính
   comment ngay trên đó (`:95-102`) đã cảnh báo hậu quả: **External không bao giờ chọn được**, và
   ở policy manual thì mọi invite thành Internal.

   Backend đã có sẵn `InviteMemberRequest.MembershipType`
   (`DTOs/WorkspaceInvitation/WorkspaceInvitationDtos.cs:10`) và endpoint `invitation-policy` trả
   `allowedMembershipTypes` / `suggestedMembershipType` / lý do disable
   (`WorkspaceInvitationPolicy.EvaluateAsync`). FE chưa dùng cái nào.

2. **Join request ở policy manual không bao giờ approve được thành Internal.**
   `EvaluateJoinRequestEligibilityAsync` (`Helpers/WorkspaceHelper.cs:151-223`) hardcode
   `requireVerifiedDomain: true` khi suy ra membership type. Ở policy manual, workspace không có
   verified domain nào → `inferredMembershipType` luôn là `External` →
   `AllowedFinalMembershipTypes` chỉ chứa `["External"]` → `ApproveJoinRequestAsync` từ chối lựa
   chọn `Internal` của Admin tại `Services/WorkspaceInvitationService.cs:815-822`.

   Nghĩa là ở policy manual, **Admin không có cách nào nhận một người vào làm Internal qua đường
   join request.** Đây là lỗi độc lập với RC-1 và sẽ vẫn còn sau khi RC-1 được sửa.

3. **Spec mô tả một đằng, copy trong UI nói một nẻo.** Spec 139 dòng 103 đã nói membership type
   *"chosen by the inviter — the system does not derive it from the email domain"*. Dòng copy ở
   `invite-member-dialog.tsx:233-234` nói ngược lại.

### RC-5 — Quyền CRUD verified domain: UI và backend không khớp

Backend đã Owner-only: `VerifiedDomainService.AddDomainAsync:58-59` và `RevokeDomainAsync:176-177`
đều `if (!roleName.IsOwner()) return OnlyOwnerCanManageDomains`.

FE thì không. Settings page tính `isOwner` ở `:244` nhưng **không dùng nó ở đâu cả** — ô nhập
domain, nút `Add Domain` và nút revoke đều gate bằng `isOwnerOrAdmin` (`:771`, `:783`, `:802`).
⇒ Admin thấy control bật sáng, gõ domain, bấm Add, và nhận `403` dưới dạng toast đỏ.

Spec còn nói chiều thứ ba: bảng permission ở
`specs/139-.../workspace-types-and-role-permissions-acceptance-criteria.md:63` ghi *"Configure
verified domains | Owner: Yes | Admin: Yes, except owner-only settings"* — tức spec cho Admin
quyền này, backend thì không. Ba nguồn, ba câu trả lời khác nhau.

### RC-6 — Admin đổi được policy, dù không đụng được vào domain

`UpdateWorkspaceSettingsAsync` chỉ chặn theo Owner đúng **một** trường:

```csharp
var ownerOnlyPolicyChanged = currentConfig.AllowExternalCollaboration != settings.AllowExternalCollaboration;
if (ownerOnlyPolicyChanged && !execRoleName.IsOwner()) return Forbidden;   // :404-408
```

`RequireVerifiedDomainForInternal` không nằm trong đó, và cổng vào chỉ là `IsOwnerOrAdmin` (`:392`).
⇒ **Admin PATCH được `requireVerifiedDomainForInternal` và đổi toàn bộ membership policy của
workspace.**

Đây là điểm mất cân đối lớn nhất, và là lý do mạnh nhất cho việc thiết kế lại UI: verified domain
được bảo vệ ở mức Owner, nhưng cái công tắc quyết định *verified domain có ý nghĩa gì hay không*
lại mở cho Admin. Bảo vệ danh sách domain mà không bảo vệ công tắc thì không bảo vệ được gì.

### RC-7 — "Một domain thuộc tối đa một workspace" chưa được bảo đảm kín

Hiện có **hai** lớp bảo vệ và chúng không đồng ý với nhau:

- **Lớp DB** — partial unique index `ON workspace.workspace_verified_domains (domain) WHERE status
  = 'verified'` (`init-db.sql:260-262`, đã áp dụng lên prod qua migration
  `008-03-06-2026-add-workspace-documents-and-glossary.sql:7-9`).
- **Lớp App** — `WorkspaceHelper.GetWorkspaceIdVerifyingDomainAsync`, lọc **thêm**
  `vd.Workspace.IsActive && vd.Workspace.DeletedAt == null` (`Helpers/WorkspaceHelper.cs:288-296`).

Bốn khe hở, xếp theo mức nghiêm trọng:

**G1 — App check nhìn vòng đời workspace, DB index thì không.**

`GetWorkspaceIdVerifyingDomainAsync` lọc thêm `vd.Workspace.IsActive && vd.Workspace.DeletedAt ==
null`. Index thì chỉ nhìn `status`. Hai lớp bất đồng ở **hai** trạng thái vòng đời, không phải một:

- **Soft-delete** — `SoftDeleteWorkspaceAsync` (`Services/WorkspaceService.cs:499-536`) chỉ set
  `DeletedAt`, **không** revoke domain nào. Row `acme.com` của A vẫn `status='verified'`.
- **Suspend** — `AdminWorkspaceService.ChangeLifecycleAsync:209` lật `IsActive = false`. Đây là
  thao tác **có thể đảo ngược** (`ReactivateAsync:147`), workspace A vẫn còn nguyên vẹn và sẽ
  quay lại.

Ở cả hai trạng thái, workspace B add `acme.com` → app check **pass** (A bị lọc ra) → INSERT →
**unique index violation** → rơi vào `catch (Exception)` chung → `500 UnexpectedError`.

Trường hợp suspend nguy hiểm hơn hẳn và tôi đã bỏ sót ở bản trước: nếu index không chặn, B sẽ giữ
`acme.com` trong lúc A đang tạm ngưng, rồi admin reactivate A — và hai workspace cùng verify một
domain. Vòng đời tạm thời không được phép nhả quyền sở hữu.

**Quyết định: xét thuần theo `status` của domain, bỏ hẳn điều kiện vòng đời workspace khỏi app
check.** Hai lớp khi đó nói cùng một câu:

| Trạng thái | `status` của domain | Workspace khác add được? |
|---|---|---|
| A đang hoạt động | `verified` | Không |
| A bị suspend | `verified` — không ai revoke | **Không** *(hôm nay: app nói được → 500)* |
| Owner của A revoke domain | `revoked` | **Được** |
| Owner của A xoá workspace | `revoked` — do 3.13 revoke theo | **Được** |

Vế cuối là thứ làm rule đóng kín: soft-delete là thao tác **cuối đường**, không có flow khôi phục
(`ChangeLifecycleAsync:187-191` từ chối mọi transition trên workspace đã xoá — `DeletedWorkspaceIsImmutable`).
Nếu delete không revoke domain thì domain đó kẹt vĩnh viễn: không ai add được, và cũng không còn
Owner nào để revoke nó. Cho delete revoke domain vừa khớp đúng "ws owner thực hiện action delete
thì ws khác add được", vừa giữ nguyên nguyên tắc "dựa trên status domain" — chỉ là đảm bảo status
được cập nhật đúng lúc.

**G2 — Overlap qua subdomain — hiện *không thể chạm tới*, vì `AllowSubdomains` là cột chết.**

Về lý thuyết: index chỉ bảo đảm trùng khớp **chuỗi chính xác**. A verify `acme.com` với
`AllowSubdomains = true`; B verify `mail.acme.com`. Hai chuỗi khác nhau → index cho qua. Nhưng
`x@mail.acme.com` khi đó là Internal ở **cả hai**: ở A qua nhánh subdomain
(`Helpers/WorkspaceHelper.cs:279`), ở B qua khớp chính xác.

Nhưng đã rà toàn bộ repo: **không có đường nào bật `AllowSubdomains` lên `true` trong sản phẩm.**

| | Có | Ở đâu |
|---|---|---|
| Cột DB | ✔ | `init-db.sql:189`, default `false` |
| Entity + EF mapping | ✔ | `Domain/Entities/Workspace.cs:25`, `WorkspaceDbContext.cs:61` |
| Logic **đọc** | ✔ | `ResolveMembershipType:248`, `IsEmailDomainVerifiedAsync:279`, trả ra qua `InvitationPolicyResponse` |
| Trong `WorkspaceSettingsDto` | ✘ | không PATCH được |
| Trong `CreateWorkspaceRequest` | ✘ | không set được lúc create |
| Bất kỳ UI nào | ✘ | grep `warptalk-web/src` chỉ ra một comment về cookie domain, không liên quan |
| Gán `= true` | chỉ trong **test fixture** | `WorkspaceInvitationServiceTests.cs:374,492`, `Integration/WorkspaceInvitationIntegrationTests.cs:195,531` |
| Trong seed | ✘ | `seed-demo.sql:301` chỉ **đọc** nó trong mệnh đề `WHERE`; không seed nào gán `true` |

⇒ Trong mọi môi trường thật, `allow_subdomains` luôn là `false`, nên nhánh subdomain ở
`WorkspaceHelper.cs:279` là **code chết**, và G2 là khe hở **tiềm ẩn**, không phải khe hở đang mở.

**Quyết định cho branch này (Q11): không làm gì cả.** Giữ nguyên cột trong DB, giữ nguyên nhánh
logic đọc, **không thêm logic BE nào** — kể cả guard overlap. Mục 3.14 bị gỡ khỏi Phase 3.

Lý do gỡ chứ không giữ "vì nó rẻ": guard đó là logic mới phục vụ một cột không ai ghi được. Thêm
nó vào bây giờ là thêm code không có đường nào chạy tới, không có cách nào kiểm chứng bằng sản
phẩm, và làm PR này rộng hơn mà không đóng được rủi ro nào đang thật sự mở.

Ghi nhận là **nợ kỹ thuật đã biết**, không phải đã giải quyết. `AllowSubdomains` vẫn là một chính
sách được đọc mà không bao giờ ghi được — đúng hình dạng đã sinh ra RC-1 và RC-6 trong plan này.
Khi nào subdomain có surface thật thì guard overlap phải đi kèm **trong cùng đợt đó**, không được
bật tính năng trước rồi vá sau. Xem §8.

**G3 — Race condition: dữ liệu đã an toàn, chỉ có mã lỗi là sai.**

Cần nói rõ phạm vi vì nó quyết định chi phí xử lý: **không có nguy cơ hai workspace cùng giữ một
domain.** Partial unique index xử lý chuyện đó ở tầng DB, và Postgres không cho hai INSERT cùng
qua. Thứ hỏng chỉ là mã lỗi: cái thua cuộc ném `DbUpdateException`, rơi vào `catch (Exception)`
chung, và trả `500 UnexpectedError` thay vì `DomainRegisteredElsewhere`.

Vì vậy **không cần** distributed lock, không cần transaction serializable, không cần retry loop.
Chỉ cần bắt riêng `DbUpdateException` có `PostgresException.SqlState == "23505"` trên
`idx_workspace_verified_domains_unique_verified` và map sang lỗi nghiệp vụ — khoảng năm dòng.

Xác suất trúng vốn thấp, nhưng **tăng lên đáng kể sau §4.5**: trước đây chỉ claim được domain email
của chính mình nên hai người không thể tranh cùng một domain; bỏ rule ownership rồi thì hai
workspace cùng nhắm `acme.com` là chuyện có thật. Cùng một `catch` cũng phủ luôn phần dư của G1.

Test: một unit test dựng sẵn exception là đủ. **Không cần** integration test đa luồng — phần đồng
bộ đã do Postgres đảm nhiệm, test lại nó là test Postgres chứ không phải test code này.

**G4 — App đã case-insensitive rồi; chỉ còn index là chưa.**

Tin tốt: tầng app đã xử lý đúng và không cần đụng tới.

- `EmailAddress` constructor hạ chữ thường toàn bộ `Value` rồi mới tách `Domain`, nên `Domain` luôn
  là chữ thường (`Domain/ValueObjects/EmailAddress.cs:38-41`).
- `PublicDomains` dùng `StringComparer.OrdinalIgnoreCase` (`:9`), nên
  `IsPublicDomainName("GMAIL.COM")` đã trả `true` (`:21`).
- Cả hai đường ghi domain đều `.ToLowerInvariant()` (`Services/VerifiedDomainService.cs:62`,
  `Services/WorkspaceService.cs:116`).

Khe hở duy nhất là index đặt trên `domain` thô thay vì `lower(domain)` — một bất biến tầng app
đang gánh cho một ràng buộc tầng DB. Bất kỳ đường nào khác (seed SQL, tool nhập liệu, migration
sau này) ghi `Acme.com` sẽ lọt qua index và tạo ra hai workspace cùng giữ một domain.

⇒ G4 thu về đúng **một migration**, không kèm thay đổi code nào.

*(Ghi chú cho tương lai, chưa phải bây giờ: index chỉ phủ `status='verified'`. Hiện mọi row đều
được tạo thẳng ở trạng thái `verified` (`Mappers/VerifiedDomain/VerifiedDomainMapper.cs:30`), nên
không có row `pending` nào. Khi WT-157 làm DNS verification thật, hai workspace sẽ cùng `pending`
được một domain và cái verify sau sẽ nổ — cần xử lý trong ticket đó.)*

*(Lặt vặt: `VerificationMethod` được ghi là `"trusted"` ở `VerifiedDomainMapper.cs:31` nhưng
`"system"` ở `WorkspaceMapper.cs:123` — hai giá trị cho cùng một khái niệm. Gộp vào mục 3.5.)*

---

## 3. Quyết định đã chốt

| # | Quyết định | Hệ quả |
|---|---|---|
| D-1 | Public email domain **được phép** tạo workspace ở policy manual. Rule public-domain chuyển vào nhánh domain-verified. | RC-3 fix theo hướng nới. Rule cấm claim public domain làm verified domain giữ nguyên. |
| D-2 | Phạm vi branch này = **toàn bộ Phase 1-5**, gồm cả invite modal và script backfill. | PR lớn; Phase 1 nên là commit riêng để revert độc lập được nếu cần. |
| D-3 | Không thêm cột `WorkspaceType`, không gọi là "non-Enterprise". Phân biệt bằng **membership assignment policy** = `require_verified_domain_for_internal`. | Bám `CONTEXT.md:7-9` và spec 139; tránh nguồn sự thật thứ ba và tránh hàm ý workspace hạng hai. |
| D-4 | `WorkspaceConfiguration.VerifiedDomains` (JSON) trở thành **mirror read-only** của bảng, giống hệt cách hai cột policy đang được mirror. | RC-2 fix. Không path nào ra quyết định từ JSON nữa. |
| D-5 | Khi không truyền `requireVerifiedDomainForInternal`, mặc định là **`true`** (domain-verified). | Khớp DB default và spec 139; policy manual phải chọn tường minh. |
| D-6 | **`WorkspaceDbContext` và các entity giữ nguyên raw scaffold output — không chứa logic.** Mọi xử lý ở service layer. | Không sửa gì trong `WorkspaceDbContext.cs`. Nếu về sau cần override mapping, dùng `WorkspaceDbContext.partial.cs` (đã có sẵn, tự ghi chú "Safe from re-scaffold"). Với RC-1 thì không cần cả hai — probe cho thấy mapping hiện tại đã đúng. |
| D-7 | **Membership type (`Internal`/`External`) do Owner/Admin quyết định tại form create invitation.** Hệ thống không suy ra từ domain. Internal/External phân biệt đầy đủ ở **cả hai** policy — ở policy manual ranh giới do người quản trị vạch, không do public/private domain vạch. | RC-4. Bỏ mọi đường inference; `membershipType` trở thành bắt buộc trên invite API. Đồng thời huỷ phương án "disable block External khi manual" trong bản plan trước — nó dựa trên giả định sai rằng manual thì mọi người đều Internal. |
| D-8 | **Cấu hình verified domain + công tắc `RequireVerifiedDomainForInternal` là Owner-only, ở cả FE lẫn BE**, và chuyển sang trang `advanced` (trang đã tồn tại, đã gate Owner-only ở `advanced/page.tsx:39-48`, đã đóng khung "high-risk operations"). | RC-5 + RC-6. Settings page giữ chỗ hiển thị **read-only** trạng thái policy cho Admin, kèm link sang advanced. Spec 139 dòng 63 phải sửa theo backend, không phải ngược lại. |
| D-9 | Create form: chọn domain-verified → hiện phần domain **ngay tại form**, giới hạn ở domain email của Owner. Domain khác thêm sau ở Advanced. | §4.5. |
| D-10 | **`RequireVerifiedDomainForInternal` là giá trị dẫn xuất, không phải cấu hình.** Bất biến: `require_verified_domain_for_internal = (số verified domain active > 0)`. Owner không có công tắc; Owner thêm/revoke domain. | Xoá toggle khỏi UI. Settings PATCH **từ chối** trường này thay vì chỉ owner-gate nó. Guard "không revoke domain cuối cùng" trở thành mâu thuẫn và phải bỏ. Xem §4.7. |
| D-11 | **New owner phải có email thuộc một trong các verified domain đang active của workspace.** Một câu, không rẽ nhánh theo loại workspace: domain-verified thì tập đó có ≥1 phần tử (đảm bảo bởi `WorkspaceService.cs:123-127` lúc create và `WorkspaceSettingsValidator:46-51` lúc PATCH, cộng D-10); manual thì tập rỗng nên ràng buộc rỗng theo nghĩa toán học — không phải bị bỏ qua. | Q7 + Q8. Ràng buộc này **chưa tồn tại** trong code — `TransferOwnershipAsync:67-71` mới chỉ chặn External, mà "không External" ≠ "domain đã verified": theo rule không-reclassify, một Internal member hoàn toàn có thể mang domain ngoài danh sách. D-11 mạnh hơn hẳn check hiện tại. |
| D-12 | Business rule về consent cho `self_asserted` domain đã chốt trong capstone report. UI là **một dialog checkbox đơn giản** kiểu Linear — đọc và tick, không phải gõ lại tên. | Q5. Không soạn văn bản mới; code tham chiếu rule đó và lưu vết mỗi lần consent. Chi tiết UI ở §4.5. |
| D-14 | **Admin bị chặn sửa verified domain ở cả BE lẫn FE** (Q6 chốt theo chiều siết, không nới). Admin vẫn **xem** được. | BE đã đúng sẵn; FE phải sửa. Spec 139 dòng 63 sửa theo backend. `ListDomainsAsync:137` giữ Owner+Admin. |
| D-15 | **Bất biến "một domain thuộc tối đa một workspace" phải kín ở cả 4 khe hở RC-7**, không chỉ dựa vào partial unique index. | Sau khi bỏ rule ownership (§4.5), đây là lớp bảo vệ thật sự duy nhất còn lại. Gồm cả một migration cho index `lower(domain)`. |
| D-13 | Danh sách public email domain là **platform policy**, không phải hằng số của Workspace service. Branch này chỉ tạo đường nối (`IPublicEmailDomainProvider`); hạ tầng platform config tách ticket. | §4.8. DB đã có `platform.system_configurations` và `privacy.policy_versions` nhưng **chưa có dòng code nào đọc** — không thể "config thay vì hardcode" ở thời điểm hiện tại. |

---

## 4. Hành vi mục tiêu

### 4.1 Create workspace

| Input | Kết quả |
|---|---|
| `requireVerifiedDomainForInternal` không truyền | Domain-verified. Domain = domain email của caller. Public domain → từ chối. |
| `= true`, `verifiedDomains` rỗng | Domain-verified. Domain = domain email của caller. Public domain → từ chối. |
| `= true`, `verifiedDomains: [d]` | Domain-verified. `d` phải trùng domain caller, không public, chưa bị workspace khác claim. |
| `= false` | Manual. Không tạo row `workspace_verified_domains`. **Public domain được chấp nhận.** |
| `= false` **và** `verifiedDomains` non-empty | Domain được claim ⇒ cột dẫn xuất thành `true` (D-10). Không có error riêng; trường trong request chỉ là ý định, danh sách domain mới quyết định. |

Hai rule về caller:

- "một internal home cho mỗi user" (`Services/WorkspaceService.cs:103`) — giữ nguyên vô điều kiện.
  `IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync` vốn chỉ đếm workspace domain-verified, nên
  workspace manual không tiêu tốn "suất internal home" của ai. Không cần sửa.
- "public domain không tạo được workspace" — chuyển vào nhánh domain-verified.

### 4.2 Membership type — ai quyết định cái gì

Đây là phần đổi nhiều nhất so với bản plan trước.

| | Domain-verified | Manual |
|---|---|---|
| Ai chọn `Internal`/`External` | Owner/Admin, tại form invite | Owner/Admin, tại form invite |
| Hệ thống có tự suy ra không | **Không** | **Không** |
| Ràng buộc lên lựa chọn `Internal` | Email phải thuộc verified domain (`AllowSubdomains` áp dụng). Public domain bị từ chối dưới error code riêng. | **Không ràng buộc** |
| Ràng buộc lên lựa chọn `External` | `AllowExternalCollaboration = true`, và External chỉ nhận role `Member` | Giống hệt |
| `Allow External Collaboration` có tác dụng không | Có | **Có** — External là lựa chọn thật ở cả hai policy |

Hệ quả cần nhớ: **`Allow External Collaboration` không bao giờ bị disable theo policy.** Bản plan
trước đề xuất disable nó ở workspace không có verified domain; đề xuất đó dựa trên giả định sai
rằng lúc đó mọi member đều Internal, và bị D-7 huỷ.

### 4.3 Phân quyền và vị trí của cấu hình domain

| Hành động | Owner | Admin | Nơi đặt |
|---|---|---|---|
| Xem policy đang áp dụng + danh sách domain | Có | **Có (read-only)** | Settings — hiển thị trạng thái + link sang Advanced |
| Thêm / revoke verified domain | Có | **Không** *(BE đã đúng, FE đang Có — RC-5)* | Advanced |
| Đổi `RequireVerifiedDomainForInternal` trực tiếp | **Không ai** — dẫn xuất (D-10) | Không ai | — |
| Bật/tắt `Allow External Collaboration` | Có | Không *(đã đúng)* | Settings (giữ nguyên chỗ cũ) |

`Allow External Collaboration` ở lại Settings vì nó không phải cấu hình domain và đã được bảo vệ
đúng. Chỉ verified domain chuyển sang Advanced.

Quy tắc:

- Block `Verified Domains` **luôn bật** ở cả hai policy (với Owner). Không disable — thêm domain
  chính là cách duy nhất chuyển từ manual sang domain-verified; disable nó sẽ khoá cứng workspace
  ở policy manual.
- Không còn toggle policy (D-10). Thay bằng dòng trạng thái read-only ngay trên danh sách domain,
  ví dụ: *"Membership is domain-verified — 2 domains active"* / *"Membership is manually assigned
  — no verified domains"*.
- Thêm domain **đầu tiên** và revoke domain **cuối cùng** đều là đổi policy → cần confirm dialog
  nói rõ chiều ảnh hưởng, và ghi audit row. Các thao tác thêm/revoke ở giữa thì không.
- Đổi policy **không** reclassify member hiện có. Confirm dialog nói rõ điều này: chuyển sang
  domain-verified sẽ khiến các invite `Internal` tới domain lạ bị từ chối từ đó trở đi, còn member
  đã có thì giữ nguyên.

### 4.5 Multi-domain: code đang chặn thứ mà schema và business rule đều cho phép

**Schema đã thiết kế multi-domain, và đó là thiết kế đúng.** `workspace.workspace_verified_domains`
(`init-db.sql:229-243`) không có ràng buộc nào giới hạn số domain trên mỗi workspace. Ràng buộc
duy nhất là partial unique index:

```sql
CREATE UNIQUE INDEX idx_workspace_verified_domains_unique_verified
ON workspace.workspace_verified_domains (domain) WHERE status = 'verified';   -- :260-262
```

Nghĩa là: **một domain thuộc tối đa một workspace; một workspace có bao nhiêu domain cũng được.**
Đúng use case `enterprise.com` + `enterprise.vn` + `enterprise.com.sg`. `SoftRevoke` set
`Status = 'revoked'` (`Domain/Extensions/WorkspaceVerifiedDomainExtensions.cs:19`) nên slot được
nhả lại khi revoke — index hoạt động đúng.

Chỗ lệch nằm ở **code**, không phải schema. Hai đường ghi domain đều bắt domain phải trùng domain
email của caller:

- `Services/WorkspaceService.cs:145-148` → `CannotVerifyUnownedDomain`
- `Services/VerifiedDomainService.cs:88-89` → `CannotVerifyUnownedDomain`

Comment tại `VerifiedDomainService.cs:76-83` thừa nhận thẳng hệ quả: *"một công ty có nhiều domain
không đăng ký hết được từ một tài khoản Owner. Đó là đánh đổi có chủ ý"* — đánh đổi đó được chọn
khi WT-157 chưa quyết định phương thức verify. Business rule hiện tại (mọi domain đã coi như pass
DNS verify, WarpTalk không chịu trách nhiệm) **thay thế tiền đề đó**, nên rule ownership phải bỏ.

#### Consent che được gì và không che được gì

Cần nói thẳng để ghi đúng vào ToS chứ không phải để tranh luận:

- **Consent che được** quan hệ WarpTalk ↔ người claim domain. Owner đã đọc, đã hiểu hệ quả, đã
  bấm. Nếu họ claim nhầm hoặc claim sai, trách nhiệm thuộc về họ. Đây chính là điều business rule
  của bạn muốn, và nó hợp lệ.
- **Consent không che được** quan hệ WarpTalk ↔ nạn nhân. Người ở `victimcorp.com` chưa bao giờ
  consent điều gì. Nếu ai đó claim `victimcorp.com`, mọi nhân viên `victimcorp.com` được mời vào
  workspace đó sau này sẽ được xếp `Internal` theo domain.
- Thứ duy nhất thật sự bảo vệ nạn nhân là **partial unique index** — nhưng nó chỉ bảo vệ nếu nạn
  nhân đã đăng ký **trước**. Về bản chất đây là first-come-first-served.

⇒ Không có cách nào đóng hoàn toàn lỗ này mà không làm verify thật (WT-157). Việc cần làm là giảm
xác suất claim nhầm/claim ẩu, và làm cho mỗi lần claim đều **truy vết được**.

#### Hướng D — multi-domain + trust tier + consent có lưu vết *(đề xuất)*

Thay hướng A/B/C của bản plan trước. Không cần migration: hai cột cần dùng đã có sẵn.

**1. Phân tầng bằng `verification_method`** (`VARCHAR(50) NOT NULL`, hiện đang hardcode `"system"`
ở `Mappers/Workspace/WorkspaceMapper.cs:123`):

| `verification_method` | Nguồn | Consent |
|---|---|---|
| `owner_email` | Domain trùng email Owner — bằng chứng sở hữu có thật | Không cần |
| `self_asserted` | Owner tự khai, business rule coi như đã pass DNS | **Bắt buộc** |
| `dns_txt` | *(để dành cho WT-157)* | Không cần |

`verification_token` (`VARCHAR(255) NOT NULL`, hiện là Guid ngẫu nhiên) giữ nguyên làm chỗ chứa
TXT record khi WT-157 làm thật.

**2. Consent phải được lưu, không phải checkbox biến mất.** Nếu business rule là "WarpTalk không
chịu trách nhiệm" thì bằng chứng consent chính là thứ duy nhất chống lưng cho câu đó — checkbox
không lưu thì câu đó không có gì đỡ.

Bảng `workspace.workspace_admin_actions` đã tồn tại và vừa vặn: `Action`, `EntityType`, `EntityId`,
`WorkspaceId`, `Reason`, `Result`, `PerformedBy`, `PerformedAt`, `BeforeSummary`/`AfterSummary`
(jsonb). Ghi một row mỗi lần claim `self_asserted`: domain, phiên bản văn bản consent, ai bấm,
lúc nào.

**3. Rule nào giữ, rule nào bỏ**

| Rule | Quyết định |
|---|---|
| `domain == caller email domain` | **Bỏ** — đây là rule chặn multi-domain |
| Domain đã được workspace khác verify → từ chối (`DomainRegisteredElsewhere`) | **Giữ**, đây là lớp bảo vệ thật duy nhất |
| Public domain không được verify | **Giữ** — gmail.com không bao giờ là domain công ty |
| Owner-only (D-8) | **Giữ** |
| Domain khớp email Owner → `owner_email`, bỏ qua consent | **Thêm** |
| Mọi domain khác → `self_asserted`, bắt consent | **Thêm** |

#### UI cho hướng D

- **Create form**: chỉ hiện domain từ email Owner (`owner_email`, chip xanh, không cần consent).
  Không cho thêm domain lạ ngay tại đây — người dùng chưa có ngữ cảnh gì về workspace để đọc hiểu
  một cảnh báo có trọng lượng. Thêm dòng dẫn: "Thêm domain khác sau, trong Advanced settings."
- **Advanced settings**: nơi duy nhất thêm được `self_asserted` domain.
  - Banner cố định của cả block, không phải dòng chữ nhỏ: WarpTalk **không** kiểm tra DNS; mỗi
    domain thêm vào là một khẳng định của Owner.
  - Thêm domain lạ → **dialog consent với một checkbox bắt buộc**, kiểu Linear: tiêu đề ngắn,
    body một câu, checkbox mang chính nội dung cam kết, nút primary disabled cho tới khi tick.
    Không dùng pattern gõ-lại-tên như delete workspace — đây là hành động cần *đọc*, không cần
    *chống nhầm tay*.

    ```
    ┌─ Add enterprise.vn as a verified domain ──────────────┐
    │                                                        │
    │  WarpTalk does not verify domain ownership. Adding a   │
    │  domain records your organization's assertion.         │
    │                                                        │
    │  ☐ I confirm my organization owns enterprise.vn, and   │
    │    I understand anyone invited with an @enterprise.vn  │
    │    address can be assigned Internal membership.        │
    │                                                        │
    │                        [ Cancel ]  [ Add domain ]      │
    └────────────────────────────────────────────────────────┘
    ```

    Cam kết nằm **trong** label của checkbox, không phải ở body phía trên — người dùng tick vào
    đúng câu họ đang đồng ý, chứ không tick vào ô trống bên dưới một đoạn văn họ đã lướt qua.
  - Danh sách domain phân biệt hai tier bằng badge: `owner_email` = "Verified from your email",
    `self_asserted` = "Self-asserted" (màu cảnh báo, có tooltip).

### 4.4 Invite — hành vi backend

- FE gọi `invitation-policy` sau khi nhập email (debounce) và **luôn** gửi `membershipType`. Cách
  trình bày lựa chọn ở form: xem §4.6.
- BE: `membershipType` trở thành **bắt buộc** trên `POST invitations`. Bỏ nhánh fallback
  `DetermineMembershipTypeAsync` ở `Services/WorkspaceInvitationService.cs:104-111`. Thiếu field →
  `400 InvalidMembershipType`. *(Đây là breaking change trên API — xem câu hỏi Q3 ở §9.)*
- `WorkspaceInvitationPolicy.ValidateAsync` giữ nguyên vai trò: **validate lựa chọn của Owner/Admin**,
  không tự chọn thay. Ở policy manual nó vốn đã trả `Success` cho `Internal` mà không kiểm tra gì
  (`:128-134`) — đúng với D-7, không cần sửa.
- Join request: `EvaluateJoinRequestEligibilityAsync` phải trả `AllowedFinalMembershipTypes` chứa
  **cả `Internal` lẫn `External`** khi policy là manual, để Admin approve được cả hai (RC-4.2).
- Xoá dòng copy sai ở `invite-member-dialog.tsx:232-235`.

### 4.6 Invite — form UI (Role + Access type)

Dropdown hiện tại thiếu External (`invite-member-dialog.tsx:223-231` chỉ có Member/Admin). Nhưng
**External không phải một role** — schema có hai trục độc lập:

- `Role`: `Owner` / `Admin` / `Member` → `WorkspaceInvitation.RoleId`
- `MembershipType`: `Internal` / `External` → `WorkspaceInvitation.MembershipType`

Và backend ép External chỉ được nhận role `Member`, ở **cả** create lẫn accept
(`WorkspaceInvitationPolicy.ValidateAsync:153-156` → `ExternalMemberMustHaveMemberRole`). Nên tổ
hợp hợp lệ chỉ có ba:

| Access type | Role | Điều kiện |
|---|---|---|
| Internal | Member | Domain-verified: email phải thuộc verified domain. Manual: không ràng buộc |
| Internal | Admin | Như trên, **và** inviter phải là Owner (`AdminCannotPromoteToAdmin`) |
| External | Member | `AllowExternalCollaboration = true` |

Hai cách trình bày:

**Chốt: một dropdown, ba lựa chọn — `Member` / `Admin` / `External`.** Không dùng chữ "guest";
`External` là tên đúng của membership type trong schema, thêm chữ nào cũng là bịa ra một khái niệm
thứ ba. Cách này không thể chọn được tổ hợp sai, khớp cách team đang nói ("role + external"), và
vẫn map sạch sang hai field API:

| Chọn | `roleName` | `membershipType` |
|---|---|---|
| Member | `Member` | `Internal` |
| Admin | `Admin` | `Internal` |
| External | `Member` | `External` |

*(Phương án hai field riêng — Access type + Role — bị loại: phải disable `Admin` khi chọn
`External`, người dùng thấy option rồi bị khoá, khó hiểu hơn hẳn.)*

Options lấy từ `invitation-policy`: option nào không hợp lệ thì disable kèm
`internalDisabledReason` / `externalDisabledReason` từ server, không tự suy ở FE.

Note dưới dropdown đổi theo lựa chọn, thay cho dòng copy sai hiện tại:

- **Member / Admin** — *"Internal member. "* + (domain-verified) *"Email phải thuộc verified domain
  của workspace."* / (manual) *"Workspace này không ràng buộc theo domain."*
- **External** — hiển thị **policy** chứ không chỉ mô tả: *"External members are always assigned
  the Member role."* + ranh giới truy cập: không xem được danh sách thành viên đầy đủ, chỉ truy cập
  tài nguyên gắn với cuộc họp họ tham gia. Đây là ràng buộc backend ép
  (`ExternalMemberMustHaveMemberRole`), nên nói ra để người dùng không đi tìm cách gán Admin cho
  External.

Nội dung note lấy từ ranh giới đã ghi trong spec 139 dòng 50 và bảng permission dòng 56-77 — không
tự chế, để copy và code không trôi khỏi nhau lần nữa.

### 4.7 Policy là giá trị dẫn xuất, không phải cấu hình (D-10)

**Bất biến:**

```
require_verified_domain_for_internal  ==  (COUNT(verified domain active) > 0)
```

Owner không có công tắc nào. Owner thêm domain → workspace thành domain-verified. Owner revoke
domain cuối cùng → workspace về manual. Policy là **hệ quả** của danh sách domain, không phải một
lựa chọn song song có thể mâu thuẫn với nó.

#### Vì sao điều này không phải là tái lập WT-179

Nhìn qua thì giống: WT-179 chính là sự cố "danh sách domain quyết định policy". Nhưng nguồn khác
nhau, và đó là toàn bộ vấn đề.

- **WT-179**: policy được suy từ `VerifiedDomains` trong **settings JSON**. JSON không được cập
  nhật khi domain bị revoke, nên workspace đã tắt policy vẫn hành xử như đang bật. Fix lúc đó:
  chỉ cột mới tính.
- **Bây giờ**: policy được suy từ **bảng `workspace_verified_domains`**, là nguồn sự thật đã được
  thiết lập ở D-4 và RC-2. `SoftRevoke` set `Status = 'revoked'`
  (`Domain/Extensions/WorkspaceVerifiedDomainExtensions.cs:19`) nên revoke phản ánh ngay.

Điều kiện tiên quyết mà ghi chú WT-179 nêu — *"one source of truth is a prerequisite"* — chính là
D-4 trong plan này, và D-4 nằm ở Phase 1, **trước** D-10 ở Phase 3. Thứ tự đó bắt buộc, không
được đảo.

Thêm nữa, D-10 **xoá bỏ** trạng thái đã gây ra WT-179: sau khi áp dụng, không tồn tại workspace
nào "có domain nhưng policy tắt". Trạng thái không tồn tại thì không lệch được.

#### Những thứ bị xoá bỏ theo

| Thứ | Số phận |
|---|---|
| Toggle `Require Verified Domain` trong UI | **Xoá.** Thay bằng dòng trạng thái read-only suy từ danh sách domain. |
| `RequireVerifiedDomainForInternal` trong settings PATCH | **Từ chối** (`400`) nếu client gửi, thay vì chỉ owner-gate. Đây là bản đầy đủ của fix RC-6. |
| Guard `CannotRevokeLastDomain` (`VerifiedDomainService.cs:187-198`) | **Bỏ.** Nó chặn đúng cái transition hợp lệ duy nhất từ domain-verified về manual; giữ lại thì workspace kẹt vĩnh viễn. Thay bằng confirm dialog ở UI + audit row, vì đây là thay đổi policy chứ không phải thao tác thường. |
| Rule "bật toggle chỉ khi có ≥1 domain" | Không còn cần — bất biến làm điều đó theo định nghĩa. |
| `VerifiedDomainsRequired` trong `WorkspaceSettingsValidator` | Giữ làm defense-in-depth, nhưng về cấu trúc nó không thể vi phạm được nữa. |

#### Nơi phải duy trì bất biến

Ba đường ghi, tất cả đều ở service layer:

1. `WorkspaceService.CreateWorkspaceAsync` — `domainsToVerify` rỗng hay không quyết định cột.
2. `VerifiedDomainService.AddDomainAsync` — sau khi thêm, cột = `true`.
3. `VerifiedDomainService.RevokeDomainAsync` — sau khi revoke, cột = `(còn domain active)`.

Nên gom vào **một** helper duy nhất trong `WorkspaceHelper`
(`RecomputeDomainPolicyAsync(uow, workspaceId, ct)`) và ba đường trên đều gọi nó, thay vì mỗi chỗ
tự set cột. Ba bản sao của một bất biến là cách WT-179 đã xảy ra lần đầu.

### 4.8 Platform policy: bảng đã có, code chưa dùng

Câu hỏi "system admin có config được platform policy này thay vì hardcode không?" — **hiện tại là
không**, dù DB đã chuẩn bị đầy đủ chỗ cho nó.

#### DB đã có sẵn

| Bảng | Nội dung | Dùng được cho |
|---|---|---|
| `platform.system_configurations` (`init-db.sql:1361-1374`) | `key` unique, `value` jsonb, `description`, `is_sensitive`, `is_active`, soft delete, `created_by`/`updated_by` | Danh sách public email domain |
| `platform.feature_flags` (`:1376-1390`) | `key`, `is_enabled`, `rollout_percentage`, `conditions` jsonb | Bật/tắt multi-domain, bật/tắt yêu cầu consent |
| `platform.service_configurations` (`:1392-1405`) | `service_name` + `config_key` + `config_value`, unique theo cặp | Cấu hình riêng cho workspace-service |
| `platform.config_change_logs` (`:1407-1416`) | `config_scope`, `config_key`, `old_value`, `new_value`, `changed_by`, `change_reason` | Audit mọi thay đổi platform policy |
| `privacy.policy_versions` (`:1297-1310`) | `policy_type`, `version`, `title`, `content`, `effective_at`, `retired_at`, `is_active` | **Văn bản consent có version** — comment ở `:2065` liệt kê `policy_type` gồm `privacy_policy, terms_of_service, voice_consent, ai_processing_notice` |

`privacy.policy_versions` đặc biệt đáng chú ý: nó chính xác là thứ cần cho D-12. Thêm một
`policy_type` mới (ví dụ `verified_domain_assertion`) là có ngay văn bản consent có version, có
`effective_at`, có lịch sử — thay vì hardcode string trong FE.

#### Nhưng chưa có dòng code nào đọc chúng

Grep toàn bộ `warptalk-backend` và `warptalk-web` cho `system_configurations`, `feature_flags`,
`service_configurations`, `policy_versions`: **không có kết quả nào ngoài chính file DDL và
schema.dbml.** Bốn bảng platform và bảng policy_versions tồn tại thuần tuý ở mức schema.

Admin surface hiện có (`web/src/app/(app)/admin/`): `billing`, `global-glossary`, `workspaces`,
dashboard. Không có trang platform config nào.

#### Chỗ đang hardcode

```csharp
// Domain/ValueObjects/EmailAddress.cs:9-14
private static readonly HashSet<string> PublicDomains = new(StringComparer.OrdinalIgnoreCase)
{
    "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "icloud.com",
    "aol.com", "zoho.com", "proton.me", "protonmail.com", "mail.com",
    "live.com", "yandex.com", "gmx.com"
};
```

Sai chỗ ở hai mức:

1. **Bất biến compile-time.** Muốn thêm một nhà cung cấp mail công cộng mới (`fastmail.com`,
   `tutanota.com`, hay một dịch vụ nội địa) phải sửa code, build, deploy toàn bộ service.
2. **Sống trong Domain layer của Workspace service**, trong khi nó là platform policy — Auth,
   Billing hay bất kỳ service nào khác cần cùng danh sách này sẽ phải copy, và bản copy sẽ trôi.

Danh sách này quyết định ai được tạo workspace domain-verified và ai không được claim domain —
đủ quan trọng để không nên nằm trong một `HashSet` hardcode.

#### Đề xuất: tạo đường nối ngay, hoãn hạ tầng

Implement platform config đầy đủ (service/API/UI/cache invalidation xuyên service) là scope riêng,
không thuộc branch này. Nhưng để lại hardcode nguyên trạng thì lần sau vẫn phải sửa 6 call site.

Làm **một** việc rẻ trong branch này: đưa danh sách ra sau một interface ở Application layer
(`IPublicEmailDomainProvider`), cài đặt mặc định trả về đúng danh sách hiện tại. Sáu call site
hiện tại (`WorkspaceService`, `VerifiedDomainService`, `WorkspaceInvitationPolicy`, và
`EmailAddress.IsPublicDomain`) chuyển sang gọi provider. Khi platform config được làm thật, chỉ
cần thay implementation — không đụng call site nào.

> **Thực tế có ba bản sao của danh sách này, không phải một.** Backend `EmailAddress.cs:9-14` (13
> domain), web `lib/workspace/email-domain.ts:1-15` (13 domain, **khớp đúng**), và một mảng inline
> **4 phần tử** ở `settings/page.tsx:352` bỏ qua chính lib dùng chung nằm cùng repo. Bản thứ ba là
> bug đang sống → mục 3.17. Provider ở đây chỉ gom được phía backend; phía web vẫn là bản sao thủ
> công cho tới khi có endpoint đọc platform config.

Phần còn lại (bảng, API, admin UI, cache) tách ticket riêng → Q10.

### 4.9 Đối chiếu hai loại theo từng action *(rà toàn bộ, không suy đoán)*

Giả thuyết cần kiểm: *"workspace manual thì mọi action liên quan verified domain đều không bị
validate."* Kết quả: **gần đúng, có ba ngoại lệ.**

| Action | Manual (0 domain) | Domain-verified (≥1 domain) | |
|---|---|---|---|
| Invite `Internal` | `ValidateAsync:128-134` → `Success` ngay, không kiểm gì | public-domain + verified-domain | ✔ đúng giả thuyết |
| Accept invitation | gọi lại `ValidateAsync` → như trên | như trên | ✔ |
| Suy ra membership type khi client không gửi | `DetermineMembershipTypeAsync:106` short-circuit → `Internal` | resolve theo domain | ✔ *(đường này bị bỏ ở 4.3)* |
| `IsUserExternalMemberAsync(email)` | `:72` → `false` ngay | so domain | ✔ |
| "Một internal home cho mỗi user" | không tính workspace này (`:57-60` chỉ đếm `require=true`) | tính | ✔ |
| `VerifiedDomainsRequired` trong settings validator | không áp (`:46`) | áp | ✔ |
| Tạo workspace từ public domain | *(sau RC-3)* cho phép | chặn | ✔ |
| Transfer ownership | *(sau D-11)* tập rỗng → không ràng buộc | phải thuộc verified domain | ✔ |
| **Invite `External`** | **vẫn validate**: `AllowExternalCollaboration` + role phải là `Member` (`ValidateAsync:148-156`) | giống hệt | ⚠️ **ngoại lệ 1** |
| **Join request — tạo** | `EvaluateJoinRequestEligibilityAsync:170-174` hardcode `requireVerifiedDomain: true` → mọi requester thành `External` | đúng theo domain | ⚠️ **ngoại lệ 2** |
| **Join request — approve** | `AllowedFinalMembershipTypes` chỉ chứa `[External]` → Admin **không** approve `Internal` được (`WorkspaceInvitationService.cs:815-822`) | đúng | ⚠️ **ngoại lệ 2** |
| **Thêm verified domain** | **vẫn validate đầy đủ**: public-domain, uniqueness | giống hệt | ⚠️ **ngoại lệ 3** |

**Ngoại lệ 1 — đúng, không sửa.** `AllowExternalCollaboration` và "External chỉ được role Member"
không phải rule về domain; chúng là rule về membership type, và D-7 đã chốt Internal/External có
nghĩa đầy đủ ở **cả hai** loại. Nên chúng phải chạy ở cả hai. Giả thuyết cần đọc chính xác là
*"không validate theo **domain**"*, không phải *"không validate gì"*.

**Ngoại lệ 2 — bug, đã có trong plan (RC-4.2, mục 4.4).** Đây là chỗ workspace manual **bị** kiểm
theo verified domain dù không có domain nào, và hậu quả là Admin không có đường nào nhận người vào
làm Internal qua join request. Chính là vi phạm nguyên tắc bạn nêu.

**Ngoại lệ 3 — đúng, không sửa.** Thêm domain là action **tạo ra** trạng thái domain-verified
(D-10), nên nó phải được validate bằng luật của loại đích, không phải loại nguồn. Nếu nới ở đây
thì workspace manual thành cửa sau để claim domain không qua kiểm tra.

#### Hai hàm chết phát hiện khi rà

| Hàm | Trạng thái |
|---|---|
| `WorkspaceHelper.DetermineJoinRequestMembershipTypeAsync:127-149` | **0 call site** trong `src`. Chứa logic hardcode `requireVerifiedDomain: true` kèm comment giải thích chủ ý — dễ đọc nhầm là đang có hiệu lực. |
| `WorkspaceHelper.IsUserExternalMemberAsync(uow, workspaceId, string userEmail, ct):63-88` | **0 call site**. Overload `Guid userId` ở `:90` mới là bản đang dùng (`WorkspaceMemberService.cs:67`). |

Cả hai đọc verified domain và trông như đang thực thi policy. Xoá trong mục 4.5 cùng với
`DetermineMembershipTypeAsync` — nếu không, lần rà sau lại phải chứng minh lại rằng chúng vô hại.

#### Hệ quả lên §4.1

Trường `requireVerifiedDomainForInternal` trong `CreateWorkspaceRequest` không còn là thứ được lưu
thẳng; nó là **ý định** ("tôi có muốn claim domain email của mình không"). Cột được suy ra từ kết
quả cuối cùng của `domainsToVerify`. Vì vậy tổ hợp mâu thuẫn ở §4.1 (`= false` + `verifiedDomains`
non-empty) không còn cần error riêng: có domain thì cột là `true`, hết. **Q1 được giải quyết theo
cách này, không phải bằng cách từ chối.**

---

## 5. Phased implementation

### Phase 1 — Nền tảng backend *(commit riêng, merge được độc lập)*

Không file nào trong `Persistence/` hay `Domain/Entities/` bị đụng tới (D-6).

| # | File | Thay đổi |
|---|---|---|
| 1.1 | `Mappers/Workspace/WorkspaceMapper.cs` | `ToEntity` nhận thêm `requireVerifiedDomainForInternal`, `allowExternalCollaboration` và gán vào entity. Đây là **toàn bộ** fix cho RC-1. |
| 1.2 | `Helpers/WorkspaceHelper.cs` | Thêm `GetActiveVerifiedDomainsAsync(uow, workspaceId, ct)`. Refactor 4 chỗ đang lặp cùng query đó (`DetermineMembershipTypeAsync`, `DetermineJoinRequestMembershipTypeAsync`, `EvaluateJoinRequestEligibilityAsync`, `IsEmailDomainVerifiedAsync`). |
| 1.3 | `Validators/WorkspaceSettingsValidator.cs` | `Validate(settings, activeVerifiedDomains)` — kiểm tra theo bảng, không theo JSON. |
| 1.4 | `Services/WorkspaceService.cs` | `GetWorkspaceSettingsAsync` gán `VerifiedDomains` từ bảng trước khi map DTO. `UpdateWorkspaceSettingsAsync` truyền list bảng vào validator và mirror list đó vào config trước khi ghi. |
| 1.5 | `Services/WorkspaceService.cs:421-455` | Bỏ block `removedDomains` — nó chỉ có nghĩa khi JSON là nguồn sự thật, và guard tương đương đã tồn tại đầy đủ trong `VerifiedDomainService.RevokeDomainAsync:200-229`. |
| 1.6 | `Domain/Settings/WorkspaceConfiguration.cs:65` | Comment `VerifiedDomains` là mirror read-only. Chỉ thêm comment — đây là settings POCO, không phải entity scaffold. |

### Phase 2 — Create workspace

| # | File | Thay đổi |
|---|---|---|
| 2.1 | `Services/WorkspaceService.cs:109-132` | Tính `requireVerified` trước, `domainsToVerify` sau. Từ chối tổ hợp mâu thuẫn ở §4.1. |
| 2.2 | `Services/WorkspaceService.cs:97` | Đưa check public-domain vào trong nhánh `requireVerified`. |
| 2.3 | `Domain/Constants/WorkspaceConstants.cs` | Thêm error constant cho tổ hợp mâu thuẫn. |
| 2.4 | `web/src/app/(app)/workspace/create/page.tsx` | Thêm lựa chọn policy (2 card / radio), copy giải thích hệ quả từng bên bằng ngôn ngữ §0 — không dùng chữ "non-Enterprise" hay "small workspace". Chọn domain-verified → hiện domain `owner_email` dưới dạng chip khoá + dòng dẫn sang Advanced để thêm domain khác (§4.5). |
| 2.5 | cùng file, `:272` | `getAccountIssue` chỉ chặn public domain khi đang chọn domain-verified. |
| 2.6 | cùng file, `:130-135` | Gửi `requireVerifiedDomainForInternal` theo lựa chọn; chỉ gửi `verifiedDomains` khi domain-verified. |

### Phase 3 — Phân quyền + chuyển cấu hình domain sang Advanced (D-8)

Backend đi trước, FE theo sau — thứ tự này quan trọng vì RC-6 là lỗ hổng đang mở.

| # | File | Thay đổi |
|---|---|---|
> **Hotfix đã tách riêng (Q4).** Nhánh `hotfix/workspace-policy-columns-not-persisted`, base
> `development`, chứa mục 1.1 + một bản thu gọn của RC-6 (owner-gate `RequireVerifiedDomainForInternal`
> trong PATCH). Mục 3.1 dưới đây **thay thế** bản thu gọn đó bằng bản đầy đủ — từ chối hẳn trường
> này vì nó là giá trị dẫn xuất. Merge hotfix trước, branch này rebase sau.

| # | File | Thay đổi |
|---|---|---|
| 3.1 | `Helpers/WorkspaceHelper.cs` | Thêm `RecomputeDomainPolicyAsync(uow, workspaceId, ct)` — nguồn duy nhất duy trì bất biến D-10. **Không** để ba service tự set cột; ba bản sao của một bất biến chính là cách WT-179 xảy ra lần đầu. |
| 3.2 | `Services/WorkspaceService.cs:404-408` | `RequireVerifiedDomainForInternal` trong PATCH → `400`, không phải owner-gate. Nó không còn là cấu hình (D-10). Thêm error constant tương ứng. |
| 3.3 | `Services/VerifiedDomainService.cs:88-89` | **Bỏ rule `domain == caller email domain`** (§4.5). Giữ `DomainRegisteredElsewhere`, giữ public-domain, giữ Owner-only. |
| 3.4 | `Services/VerifiedDomainService.cs:187-198` | **Bỏ guard `CannotRevokeLastDomain`** — nó chặn đúng transition hợp lệ duy nhất về manual (D-10). Gọi `RecomputeDomainPolicyAsync` sau revoke. Giữ nguyên guard "active internal members" ở `:200-229`. |
| 3.5 | `Services/VerifiedDomainService.cs` (AddDomain) | Gọi `RecomputeDomainPolicyAsync` sau khi thêm. Đặt `verification_method` = `owner_email` khi domain trùng email Owner, `self_asserted` khi khác. Bỏ hardcode ở **cả hai** chỗ: `"system"` tại `Mappers/Workspace/WorkspaceMapper.cs:123` **và** `"trusted"` tại `Mappers/VerifiedDomain/VerifiedDomainMapper.cs:31` — bản trước của bảng này chỉ liệt kê chỗ đầu. |
| 3.6 | `Services/VerifiedDomainService.cs` (AddDomain) | Với `self_asserted`: yêu cầu consent tường minh từ request; thiếu → `400`. Ghi row `workspace_admin_actions` (domain, rule version theo capstone report, ai, lúc nào) — D-12. |
| 3.13a | `Helpers/WorkspaceHelper.cs:288-296` | **RC-7/G1**: bỏ điều kiện `Workspace.IsActive && Workspace.DeletedAt == null` khỏi `GetWorkspaceIdVerifyingDomainAsync`. Uniqueness xét thuần theo `status` của domain, khớp đúng partial unique index. Sửa luôn case suspend. |
| 3.13b | `Services/WorkspaceService.cs:499-536` (`SoftDeleteWorkspaceAsync`) | **RC-7/G1**: revoke toàn bộ verified domain khi soft-delete. Bắt buộc, vì sau 3.13a domain của workspace đã xoá sẽ kẹt vĩnh viễn — không ai add được và cũng không còn Owner nào revoke được (`ChangeLifecycleAsync:187-191` chặn mọi transition trên workspace đã xoá). |
| ~~3.14~~ | — | **Đã gỡ (Q11).** Guard overlap subdomain không làm trong branch này: đó là logic mới cho một cột không ai ghi được. Xem RC-7/G2 và §8. |
| 3.15 | `Services/VerifiedDomainService.cs` + `Services/WorkspaceService.cs` (catch block) | **RC-7/G3**: bắt riêng `DbUpdateException` có `PostgresException.SqlState == "23505"` → trả `DomainRegisteredElsewhere` thay vì `500`. Chỉ sửa mã lỗi; **không** thêm lock/transaction/retry — Postgres đã đảm bảo tính đúng đắn. |
| 3.16 | `warptalk-infrastructure/scripts/migrations/` *(migration mới)* | **RC-7/G4**: đổi index sang `ON (lower(domain)) WHERE status = 'verified'`. Kèm pre-check liệt kê domain trùng khi bỏ qua hoa/thường — phải sạch trước khi tạo index. **Không kèm thay đổi code**: tầng app đã case-insensitive sẵn. |
| 3.12 | `Application/Interfaces/IPublicEmailDomainProvider.cs` *(mới)* + 6 call site | **D-13**: đưa `EmailAddress.PublicDomains` (`Domain/ValueObjects/EmailAddress.cs:9-14`) ra sau interface ở Application layer, implementation mặc định trả đúng danh sách hiện tại. Chuyển `WorkspaceService`, `VerifiedDomainService`, `WorkspaceInvitationPolicy` sang gọi provider. Không đổi hành vi — chỉ tạo đường nối để platform config sau này thay implementation mà không đụng call site. |
| 3.7 | `Services/WorkspaceService.cs:145-148` | Create chỉ nhận domain `owner_email`; domain khác đi qua Advanced (§4.5). Gọi `RecomputeDomainPolicyAsync` cuối flow. |
| 3.8 | `Services/WorkspaceMemberService.cs:67-71` | **D-11**: new owner phải có email thuộc verified domain active của workspace. Cài đúng một câu — lấy danh sách qua `GetActiveVerifiedDomainsAsync` (1.2), rỗng thì pass. Không viết `if (requireVerifiedDomainForInternal)`; rẽ nhánh theo cột là mở lại đúng loại lệch mà D-10 vừa đóng. Giữ nguyên check External hiện có, D-11 nằm thêm bên cạnh chứ không thay thế. Cần error constant mới. |
| 3.9 | `web/.../advanced/page.tsx` | Section `Membership policy` (ngoài Danger zone): trạng thái policy read-only + CRUD domain, banner cảnh báo không kiểm tra DNS, dialog consent gõ-lại-domain, badge phân tier (§4.5). Confirm dialog riêng khi thêm domain **đầu tiên** / revoke domain **cuối cùng** vì đó là đổi policy. Trang đã Owner-only sẵn ở `:39-48`. |
| 3.10 | `web/.../settings/page.tsx:746-812` | Bỏ hẳn toggle `Require Verified Domain` và block CRUD domain. Thay bằng khối read-only: trạng thái policy + danh sách domain + link sang Advanced (link chỉ hiện cho Owner). |
| 3.11 | `web/.../settings/page.tsx:244` | `isOwner` hiện tính rồi bỏ không; sau 3.10 nó dùng cho khối read-only. Nếu không còn call site thì xoá. |
| 3.17 | `web/.../settings/page.tsx:352` | **Bug đang sống**: `handleAddDomain` tự khai một mảng public domain **4 phần tử** inline, trong khi `web/src/lib/workspace/email-domain.ts:1-15` đã có `PUBLIC_EMAIL_DOMAINS` **13 phần tử** khớp đúng backend. ⇒ gõ `proton.me` lọt validation FE rồi ăn `403 CannotVerifyPublicDomain`. Xoá mảng inline, dùng `isPublicEmailDomain` từ lib dùng chung — cùng lúc chuyển block sang Advanced ở 3.9. |

### Phase 4 — Membership type do Owner/Admin quyết định (D-7)

| # | File | Thay đổi |
|---|---|---|
| 4.1 | `web/src/hooks/use-workspace.ts` | Hook gọi `invitation-policy` (debounce theo email). |
| 4.2 | `web/src/components/workspace/invite-member-dialog.tsx` | Dropdown 3 lựa chọn theo §4.6 (`Member` / `Admin` / `External` — không dùng chữ "guest"), disable theo `allowedMembershipTypes` + `canGrantAdmin`, note đổi theo lựa chọn và nêu rõ policy "External luôn ở role Member", luôn gửi cả `roleName` lẫn `membershipType`, xoá copy sai ở `:232-235`. |
| 4.3 | `Services/WorkspaceInvitationService.cs:104-111` | Bỏ fallback `DetermineMembershipTypeAsync`; `membershipType` thiếu → `400`. |
| 4.4 | `Helpers/WorkspaceHelper.cs:151-223` | `EvaluateJoinRequestEligibilityAsync`: khi policy manual, `AllowedFinalMembershipTypes` phải chứa cả `Internal` và `External` (RC-4.2). |
| 4.5 | `Helpers/WorkspaceHelper.cs` | Xoá code chết đọc verified domain (§4.9): `DetermineMembershipTypeAsync:98-125` sau khi 4.3 bỏ call site cuối; `DetermineJoinRequestMembershipTypeAsync:127-149` (0 call site); overload `IsUserExternalMemberAsync(…, string userEmail, …):63-88` (0 call site — bản `Guid userId` ở `:90` mới là bản đang dùng). Cả ba trông như đang thực thi policy nhưng không chạy. |

### Phase 5 — Spec + dữ liệu

| # | File | Thay đổi |
|---|---|---|
| 5.1 | `specs/139-.../workspace-types-and-role-permissions-acceptance-criteria.md` | Bổ sung nhánh `RequireVerifiedDomainForInternal = false` vào Acceptance Criteria, dùng thuật ngữ §0. Viết lại Out of Scope dòng 147 — đây là giá trị policy đã tồn tại, không phải workspace type mới. Sửa bảng permission dòng 63: "Configure verified domains" là **Owner-only**, Admin `No` (RC-5). Ghi rõ `RequireVerifiedDomainForInternal` là giá trị dẫn xuất, không ai cấu hình trực tiếp (D-10). Bổ sung ràng buộc transfer ownership trong cùng verified domain (D-11) vào mục Ownership Rules. |
| 5.2 | `specs/140-workspace-invitations/spec.md` | Đồng bộ với D-7: inviter chọn membership type, hệ thống chỉ validate. |
| 5.3 | `specs/workspace-module-requirements/CONTEXT.md` | Thêm hai thuật ngữ "domain-verified membership" / "manually-assigned membership" vào mục Language, kèm _Avoid_: "non-Enterprise workspace", "small workspace". |
| 5.4 | `warptalk-infrastructure/scripts/` | Script backfill cho workspace tạo qua API trước fix, hai phần: (a) `allow_external_collaboration = false` trong khi JSON nói `true`; (b) `require_verified_domain_for_internal = false` trong khi workspace có row `workspace_verified_domains` active và/hoặc JSON nói `true`. Phần (b) là thay đổi policy trên dữ liệu thật — dry-run liệt kê row bị ảnh hưởng và số member Internal có domain ngoài danh sách verified trước khi chạy. |

---

## 6. Test plan

**Phase 1**

- Create với `requireVerifiedDomainForInternal: true` → `GetWorkspaceConfig` ra `true`. Đây là
  test bắt RC-1.1 và là case đang hỏng trên production; hiện không có test nào chạm cột sau create.
- Create với `= false` → `GetWorkspaceConfig` ra `false`, 0 row trong `workspace_verified_domains`.
- Create không truyền gì → `RequireVerifiedDomainForInternal == true` (D-5) và
  `AllowExternalCollaboration == true`.
- Validator: bảng có domain / JSON rỗng → **pass**. JSON có domain / bảng rỗng → **fail**.
- Regression: `WorkspaceServiceTests`, `WorkspaceInvitationServiceTests`,
  `WorkspaceSettingsValidatorTests` (3 call site cần đổi signature), `WorkspaceHelperTests`,
  `VerifiedDomainServiceTests`, `AdminWorkspaceServiceTests`.

**Phase 2**

- Caller gmail + policy manual → tạo được, 0 row verified domain.
- Caller gmail + domain-verified → `PublicEmailDomainCannotCreateWorkspace`.
- `= false` + `verifiedDomains: ["acme.com"]` → từ chối.
- Caller `acme.com` claim `victimcorp.com` → vẫn `CannotVerifyUnownedDomain` (không được nới).

**Phase 4 (D-7 — phần dễ regress nhất)**

- Owner `@enterprise.com` add domain `enterprise.vn` → **thành công**, `verification_method =
  self_asserted`, có row `workspace_admin_actions`. **Đây là case đang bị chặn hôm nay** (§4.5).
- Add `enterprise.vn` mà không kèm `consentVersion` → `400`.
- Add domain đã được workspace khác verify → `DomainRegisteredElsewhere` (rule giữ lại).
- Add `gmail.com` → `CannotVerifyPublicDomain` (rule giữ lại).
- Domain trùng email Owner → `verification_method = owner_email`, không đòi consent.
- Invite `alice@enterprise.vn` với `Internal` ở policy domain-verified → thành công sau khi
  `enterprise.vn` được add (multi-domain regression test).
- Policy manual, invite `bob@gmail.com` với `membershipType: Internal` → **thành công**.
- Chọn `External guest` → request gửi `roleName: Member` + `membershipType: External`; không có
  đường nào ở UI tạo được tổ hợp External + Admin (§4.6).
- Policy manual, invite với `membershipType: External`, `AllowExternalCollaboration = true` →
  thành công. Với `= false` → `ExternalCollaborationNotAllowed`.
- Policy domain-verified, invite `bob@gmail.com` với `Internal` →
  `CannotInviteInternalWithPublicDomain`. Với `External` → thành công.
- Invite không kèm `membershipType` → `400` (không còn inference).
- Join request ở policy manual → Admin approve được thành `Internal` (RC-4.2 regression test).
- `invitation-policy` ở policy manual → `allowedMembershipTypes` chứa cả hai.

**Phase 3 (bất biến D-10 + phân quyền)**

- Bất biến sau mỗi thao tác: create có domain → cột `true`; create không domain → `false`; add
  domain đầu tiên → `true`; revoke domain cuối cùng → `false`; revoke domain không phải cuối →
  vẫn `true`. Năm case này là hợp đồng của D-10.
- Bất kỳ ai PATCH `requireVerifiedDomainForInternal` → `400` (kể cả Owner). Trường này không còn
  là cấu hình. **Regression test cho RC-6**; hiện tại Admin đang PATCH thành công.
- Admin PATCH các trường khác (ngôn ngữ, retention…) → vẫn `200`, không bị siết nhầm.
- Revoke domain cuối cùng khi workspace đang domain-verified → **thành công** (guard
  `CannotRevokeLastDomain` đã bỏ), và cột về `false`.
- Revoke domain còn active internal member thuộc domain đó → vẫn bị chặn (guard này giữ).
- Transfer ownership sang member có email ngoài verified domain → **từ chối** (D-11). Sang member
  trong verified domain → thành công.
- Transfer trong workspace **manual** (0 domain) → thành công với bất kỳ member Internal nào. Test
  này chứng minh ràng buộc rỗng thật sự rỗng, chứ không phải bị chặn nhầm bởi một danh sách trống.

**Phase 3 — bất biến uniqueness (D-15, RC-7)**

Bốn test này ứng với bốn khe hở; cả bốn đều fail trên code hiện tại.

- **G1a** — A giữ `acme.com` rồi bị **suspend** → B add `acme.com` **bị từ chối**
  `DomainRegisteredElsewhere`. Reactivate A → A vẫn giữ nguyên domain. (Hôm nay: B nhận `500`.)
- **G1b** — A giữ `acme.com` rồi bị **soft-delete** → domain của A ở `status='revoked'`, và B add
  `acme.com` **thành công**. (Hôm nay: B nhận `500`.)
- **G1c** — Owner của A revoke `acme.com` thủ công → B add được. (Đã đúng sẵn; thêm test để khoá.)
- **G2** — không có test, vì không có code mới (Q11).
- **G3** — unit test dựng sẵn `DbUpdateException`/`SqlState 23505` → service trả
  `DomainRegisteredElsewhere`, không phải `500`. Không viết test đa luồng.
- **G4** — sau migration 3.16: A giữ `acme.com`, insert thẳng `ACME.com` bằng SQL → bị index chặn.
  Test phải đi vòng qua tầng app (vì app đã hạ chữ thường sẵn) mới chứng minh được index làm việc.
- Admin gọi `POST verified-domains` → `403` (BE đã đúng, thêm test để khoá lại).
- Admin `GET verified-domains` → `200` (read-only vẫn phải xem được, `ListDomainsAsync:137`).
- Owner làm cả bốn thao tác trên → thành công.

**Phase 3-4 e2e**: tạo workspace manual → Settings hiện khối read-only đúng → Owner vào Advanced
Owner vào Advanced add domain → trạng thái policy tự đổi sang domain-verified, không có toggle nào
để bấm → add thêm `enterprise.vn` qua dialog consent → invite chọn được cả Internal lẫn External ở
cả hai policy. Đăng nhập bằng Admin → không thấy control nào của domain trên cả hai trang.

---

## 7. Rủi ro

| Rủi ro | Xử lý |
|---|---|
| Dữ liệu production đang ở trạng thái sai policy (RC-1.1) | Backfill 5.4 phải xử lý **cả hai** cột. Đây là thay đổi policy trên dữ liệu thật → review riêng và thông báo Owner bị ảnh hưởng. |
| Backfill làm member hiện tại thành không-hợp-lệ | Không reclassify (§8). Member giữ nguyên `MembershipType`; chỉ invite/join mới chịu policy. Dry-run liệt kê trước số member Internal có domain ngoài danh sách verified. |
| `membershipType` thành bắt buộc là breaking change | Client duy nhất là web app trong repo này, sửa cùng PR (4.2). Nếu có client ngoài → xem Q3 §9. |
| Bỏ block `removedDomains` làm mất guard | Guard "active internal members" đã có ở `VerifiedDomainService.RevokeDomainAsync:200-229`. Đã chốt Q2. |
| Client cũ không gửi `requireVerifiedDomainForInternal` | D-5 giữ mặc định `true`; và D-10 khiến trường này chỉ còn là ý định, cột luôn suy từ danh sách domain. |
| D-10 nhìn giống tái lập WT-179 | Không phải — nguồn khác nhau. Giải thích đầy đủ ở §4.7. Điều kiện tiên quyết là D-4 (một nguồn sự thật), và D-4 ở Phase 1 phải merge **trước** D-10 ở Phase 3. Thứ tự này không được đảo. |
| Bỏ guard `CannotRevokeLastDomain` | Đây là guard duy nhất bị gỡ mà không có bản thay thế ở tầng service — nó mâu thuẫn trực tiếp với D-10. Bù bằng confirm dialog + audit row ở UI. Nếu team thấy chưa đủ thì phương án còn lại là bắt nhập lại tên workspace như flow delete. |
| D-11 khoá workspace không transfer được | Xem Q9 §9 — chưa có lối thoát. |
| Migration index `lower(domain)` (3.16) fail vì dữ liệu prod đã có trùng | Pre-check bắt buộc trước khi tạo index; nếu có trùng thì phải quyết thủ công giữ row nào trước khi chạy migration. Không tự động chọn. |
| Revoke domain khi soft-delete workspace (3.13b) là hành vi mới trên flow đã có | Chỉ chạy trong `SoftDeleteWorkspaceAsync`, và workspace đó là trạng thái cuối đường — `ChangeLifecycleAsync:187-191` đã chặn mọi transition trên workspace đã xoá, nên không có flow khôi phục nào để mâu thuẫn. |
| 3.13a nới app check có thể lộ domain của workspace đang suspend | Ngược lại — nó **siết**: hôm nay app check bỏ qua workspace suspend nên tưởng domain còn trống. Sau 3.13a, domain của workspace suspend vẫn được giữ cho tới khi reactivate. |

---

## 8. Out of scope

- Cột `WorkspaceType` hay entity type mới (D-3).
- Verification thật cho domain (DNS TXT / email challenge) — WT-157 vẫn để ngỏ; hiện vẫn dùng
  quyền sở hữu email của caller làm bằng chứng.
- Reclassify member khi đổi policy.
- Entitlement / quota cho workspace manual tạo từ email public.
- **`AllowSubdomains` — nợ kỹ thuật đã biết, cố ý không đụng (Q11).** Cột ở lại DB, nhánh logic đọc
  ở `WorkspaceHelper.cs:248,279` ở lại nguyên trạng, **không thêm logic BE nào**. Trạng thái hiện
  tại: không có đường ghi nào trong sản phẩm (`WorkspaceSettingsDto` không có, `CreateWorkspaceRequest`
  không có, không UI, không seed nào gán `true` — chỉ test fixture), nên nhánh subdomain là code
  chết và khe hở overlap RC-7/G2 chưa thể chạm tới.

  Điều kiện khi mở lại: nếu ai đó cho `AllowSubdomains` một surface, guard overlap **phải đi cùng
  đợt đó**. Bật tính năng trước rồi vá sau là mở đúng khe hở này ra sản phẩm. Ghi ở đây để lần sau
  không phải khám phá lại.

---

## 9. Quyết định đã chốt và câu hỏi còn lại

| # | Chốt |
|---|---|
| Q1 | Policy là **giá trị dẫn xuất** từ danh sách domain, không phải cấu hình → D-10, §4.7. Tổ hợp "mâu thuẫn" không còn tồn tại nên không cần error riêng. |
| Q2 | Xoá block `removedDomains` — đồng ý. |
| Q3 | `membershipType` bắt buộc trên `POST invitations` — đồng ý. |
| Q4 | Tách hotfix, PR base `development` — đã thực hiện. |
| Q5 | Business rule consent đã chốt trong capstone report → D-12. |
| Q7 | Transfer ownership ràng buộc trong cùng verified domain → D-11, nên domain của workspace không đổi qua flow transfer. |
| Q8 | Không có hai rule. D-11 là **một câu** áp dụng đồng nhất; workspace manual có tập verified domain rỗng nên ràng buộc rỗng theo nghĩa toán học. Không viết nhánh `if` theo loại workspace. |
| Q11 | Giữ nguyên cột `allow_subdomains` trong DB, **không thêm logic BE** trong branch này. Mục 3.14 gỡ bỏ. Ghi nhận là nợ kỹ thuật ở §8, kèm điều kiện phải thoả khi mở lại. |
| — | Dropdown invite: `Member` / `Admin` / `External`, bỏ chữ "guest", hiển thị policy "External luôn ở role Member" → §4.6. |

| Q6 | Chặn Admin sửa verified domain ở **cả BE lẫn FE** → D-14. Admin vẫn xem được. |
| — | Bất biến "một domain một workspace" phải kín ở cả 4 khe hở → D-15, RC-7. |

### Còn treo
- **Q10** *(mới, từ §4.8)* — Hạ tầng platform config: mở ticket riêng ngay, hay để sau capstone?
  Bốn bảng `platform.*` và `privacy.policy_versions` đã có trong schema nhưng chưa có code. Nếu
  làm, phạm vi tối thiểu là: đọc `system_configurations` cho danh sách public domain, đọc
  `policy_versions` cho văn bản consent (`policy_type = verified_domain_assertion`), một trang
  admin để sửa, và ghi `config_change_logs` mỗi lần đổi. Cache và invalidation xuyên service là
  phần khó nhất — danh sách public domain được đọc trên đường create workspace và invite, nên
  không thể query DB mỗi lần.
- **Q9** *(thu hẹp)* — **Ở vòng đời bình thường D-11 không bao giờ fire.** Mọi Internal member của
  workspace domain-verified đều có domain thuộc list, vì `WorkspaceInvitationPolicy.ValidateAsync:126-146`
  chặn ở create và accept path re-check lại. Tập ứng viên = toàn bộ Internal member.

  D-11 chỉ fire ở ba trạng thái bất thường, và cả ba đều do plan này tạo ra:

  | | Tình huống | Có thật? |
  |---|---|---|
  | (a) | Manual → domain-verified. Owner `boss@gmail.com` tạo workspace manual (D-1 cho phép public domain), sau đó add `acme.com` dạng `self_asserted` ở Advanced → D-10 flip sang domain-verified. Không ai bị reclassify (§8). **Kể cả Owner hiện tại cũng không thuộc verified domain** → tập ứng viên rỗng. | **Có**, và D-10 làm nó chỉ cách một cú click |
  | (b) | Backfill 5.4 flip workspace sang domain-verified trong khi Internal member mang domain tuỳ ý. | Có — đúng rủi ro đã ghi ở §7 |
  | (c) | Workspace một thành viên. | Có sẵn từ trước; `NewOwnerMustBeActiveMember` đã chặn, D-11 không thêm gì |

  Vậy câu hỏi không phải "thoát kẹt bằng cách nào" mà là **D-11 nên fire hay nên nhường** khi tập
  ứng viên rỗng:

  | Phương án | Nội dung | Đánh giá |
  |---|---|---|
  | **A — Bỏ D-11** | Giữ nguyên check "không External". | Ở trạng thái khoẻ mạnh D-11 vốn đã là no-op, nên bỏ đi không mất gì *ở đó*. Nhưng mất luôn tác dụng bảo mật ở (a)/(b): workspace claim `acme.com` với nhãn `owner_email` của `alice@acme.com` mà chuyển được cho `bob@evil.com` thì nhãn đó thành lời nói dối, và bob thừa hưởng quyền tự động xếp Internal cho mọi người `@acme.com`. |
  | **B — Suy giảm thay vì khoá** *(đề xuất)* | Nếu có ≥1 member thuộc verified domain → chỉ được chuyển cho nhóm đó. Nếu **không có ai** → cho chuyển cho bất kỳ Internal member nào, kèm audit row ghi rõ đã đi đường suy giảm. | Fire đúng lúc nó bảo vệ được thứ gì đó, nhường đúng lúc nó chỉ còn khoá cửa. Không tạo trạng thái kẹt nào. |
  | **C — Giữ nghiêm + lối thoát** | Giữ D-11 nghiêm, nới guard revoke để Owner revoke hết domain về manual rồi transfer. | Đường thoát dài và khó hiểu, lại phải nới một guard khác đang làm đúng việc. |

  Đề xuất **B**. Kèm hai việc để (a) đừng xảy ra ngay từ đầu: cảnh báo ở dialog consent khi domain
  sắp add không khớp email của bất kỳ member nào, và dry-run của 5.4 liệt kê sẵn workspace sẽ rơi
  vào trạng thái này.
