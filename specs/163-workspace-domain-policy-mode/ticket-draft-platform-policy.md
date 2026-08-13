# DRAFT — chưa tạo trên Linear

> Bản nháp để review. Không tạo/sửa gì trên Linear cho tới khi được duyệt.
> Ngày soạn: 2026-08-13. Backend ở branch `fix/workspace-verified-domain-mode`.

**Module / labels**: `[system admin]`, `[platform policy]`
**Priority**: High
**Assignee**: `tuhuynh`
**Liên quan**: `specs/163-workspace-domain-policy-mode/plan.md` (Q10 → `plan.md:867-873`, D-13 → `plan.md:297`)

---

## Title

System admin configure được platform policy: bảng `platform.*` đã có schema nhưng chưa có dòng code nào đọc

---

## Problem

Bốn bảng `platform.*` và `privacy.policy_versions` tồn tại trong schema từ đầu dự án và **không có một dòng code nào đọc chúng**. Grep toàn bộ `warptalk-backend`, `warptalk-web`, `warptalk-ai` cho `system_configurations`, `feature_flags`, `service_configurations`, `config_change_logs`, `policy_versions` chỉ ra file DDL (`warptalk-infrastructure/scripts/init-db.sql`), file `.dbml`, và chính `plan.md`. Grep cho tên entity C# tương ứng (`SystemConfiguration`, `FeatureFlag`, `ServiceConfiguration`, `ConfigChangeLog`, `PolicyVersion`): **không có kết quả nào**.

Hệ quả cụ thể, không phải trừu tượng:

**1. Thêm một nhà cung cấp email công cộng = sửa code + build + deploy 1 service, và sửa 3 chỗ ở 2 repo.**

Danh sách public email domain hiện có **ba bản sao**, và **một bản đã trôi**:

| Bản sao | Số domain | Nội dung |
|---|---|---|
| `workspace/src/WarpTalk.WorkspaceService.Domain/ValueObjects/EmailAddress.cs:9-14` | 13 | nguồn "chính" |
| `warptalk-web/src/lib/workspace/email-domain.ts:1-15` | 13 | khớp chính xác — copy tay |
| `warptalk-web/src/app/(app)/[workspaceSlug]/settings/page.tsx:352` | **4** | `["gmail.com", "yahoo.com", "hotmail.com", "outlook.com"]` |

Bản thứ ba nằm trong `handleAddDomain` (`settings/page.tsx:345-356`) và thiếu 9 domain: `icloud.com`, `aol.com`, `zoho.com`, `proton.me`, `protonmail.com`, `mail.com`, `live.com`, `yandex.com`, `gmx.com`. Owner gõ `proton.me` vào ô Add Domain → FE cho qua → backend từ chối ở `VerifiedDomainService.cs:64` → toast đỏ. Đây là drift đã xảy ra rồi, không phải rủi ro lý thuyết.

**2. Business rule "trial workspace tối đa 5 member" là một `const int` trong Domain layer.**

`WorkspaceConstants.cs:19` (`TrialWorkspaceMemberLimit = 5`), đọc ở `WorkspaceInvitationService.cs:953` và `WorkspaceInvitationAcceptanceProcessor.cs:182`. Message lỗi ở `WorkspaceConstants.cs:111` còn hardcode luôn con số 5 vào chuỗi tiếng Anh. Sales muốn nới trial lên 10 cho một chiến dịch → phải deploy lại workspace service.

**3. Một tenant không thể tắt external LLM, kể cả khi UI cho họ tick.**

`WorkspaceConfiguration.NormalizeAiUsagePolicy` (`Domain/Settings/WorkspaceConfiguration.cs:82-95`) kết thúc bằng:

```csharp
: value with { AllowExternalLlm = true };   // :94
```

Nghĩa là mọi giá trị `AllowExternalLlm` do workspace gửi lên đều bị ghi đè thành `true`, không điều kiện, không comment giải thích. Giá trị này được ba đường tiêu thụ: `WorkspaceDirectoryService.cs:239`, `DocumentSecurityGuardrailConsumerService.cs:302`, `WorkspaceGrpcService.cs:198`. Đây là một platform policy ("WarpTalk hiện chỉ có đường LLM ngoài") được đóng đinh vào Domain layer của Workspace service, và nó **im lặng** — `AiUsagePolicyDto` vẫn nhận field đó qua patch (`WorkspaceMapper.cs:115`).

**4. Không có admin surface nào để sửa bất kỳ thứ gì ở trên.**

Admin portal hiện có đúng 4 mục (`warptalk-web/src/components/layout/linear-sidebar.tsx:736-787`): Overview, Workspaces, Billing, Global Glossary. Không có trang platform config. Backend cũng vậy — xem §"Baseline".

**5. Không có nơi ghi vết khi policy đổi.** `platform.config_change_logs` (`init-db.sql:1407-1416`) có sẵn `config_scope`, `config_key`, `old_value`, `new_value`, `changed_by`, `change_reason` — và trống rỗng vì chưa ai ghi vào.

---

## Trạng thái schema hiện tại (đã verify bằng đọc file)

### Dead schema — có DDL, không có code

| Bảng | Dòng | Cột đã có (dùng lại, đừng bịa cột mới) |
|---|---|---|
| `platform.system_configurations` | `init-db.sql:1361-1374` | `key` UNIQUE, `value` JSONB, `description`, `is_sensitive`, `is_active`, `created_at/by`, `updated_at/by`, `deleted_at/by` |
| `platform.feature_flags` | `init-db.sql:1376-1390` | `key` UNIQUE, `description`, `is_enabled`, `rollout_percentage`, `conditions` JSONB, `is_active`, audit + soft delete |
| `platform.service_configurations` | `init-db.sql:1392-1405` | `service_name`, `config_key`, `config_value` JSONB, `is_sensitive`, `is_active`, audit + soft delete. UNIQUE `(service_name, config_key)` ở `init-db.sql:1728` |
| `platform.config_change_logs` | `init-db.sql:1407-1416` | `config_scope`, `config_key`, `old_value` JSONB, `new_value` JSONB, `changed_by`, `change_reason`, `created_at` |
| `privacy.policy_versions` | `init-db.sql:1297-1310` | `policy_type`, `version`, `title`, `content`, `effective_at`, `retired_at`, `is_active`, audit. Index ở `:1716-1718`. Comment `:2065` liệt kê `policy_type`: `privacy_policy, terms_of_service, voice_consent, ai_processing_notice` |
| `platform.supported_languages` | `init-db.sql:1344-1359` | **Cũng dead.** Translation-room đọc `translation_room.supported_languages`, không phải bảng này — `TranslationRoomDbContext.cs:106` map `entity.ToTable("supported_languages", "translation_room")` |

Không bảng nào trong số này có `platform.*` được service nào đọc. `platform.audit_logs`, `platform.activity_logs`, `platform.outbox_events`, `platform.inbox_events`, `platform.service_health_checks`, `platform.service_deployments` cũng nằm trong schema nhưng không thuộc phạm vi ticket này.

### 🔴 Chặn đường: `platform` không thuộc logical database nào

Đây là phát hiện quan trọng nhất và nó **thay đổi hình dạng công việc**.

`extract-logical-databases.sh:160-168` liệt kê chính xác schema nào đi vào database nào:

```
warptalk_auth              "public auth voice"
warptalk_workspace         "public workspace"
warptalk_translation_room  "public translation_room"
warptalk_transcript        "public transcript"
warptalk_notification      "public notification"
warptalk_meeting           "public meeting"
warptalk_assistant         "public assistant"
warptalk_billing           "public subscription"
```

`platform` và `privacy` **không có trong danh sách**. Hợp đồng ownership ở `check-database-boundaries.sh:182-200` cũng chỉ enumerate 9 schema (`auth, voice, workspace, translation_room, transcript, notification, meeting, assistant, subscription`) — `platform` và `privacy` không thuộc role nào và không bị check gì.

⇒ Sau khi tách logical database, các bảng `platform.*` **không nằm trong database của bất kỳ service nào**. Ticket này không thể chỉ "đọc bảng có sẵn" — bước đầu tiên phải là quyết định service nào **sở hữu** schema `platform` và viết migration tạo bảng trong database của service đó. Xem Open question OQ-1.

---

## Baseline: system admin config được gì hôm nay

**Cổng chung**: `WarpTalk.Shared/Authorization/SystemAdminAuthorization.cs` — policy name `"WarpTalkSystemAdmin"` (`:16`), role claim chính xác `"admin"` chữ thường (`:22`), phân biệt với role workspace `"Admin"` chữ hoa (`:43-48`). WT-205 đã chuẩn hoá cái này, tái sử dụng được nguyên vẹn.

**Backend `/api/v1/admin/*`**:

| Route | File | Nội dung |
|---|---|---|
| `api/v1/admin/workspaces` | `AdminWorkspacesController.cs:26-27` | monitoring tenant — **out of scope** |
| `api/v1/admin/audit-log` | `AdminAuditLogController.cs:21-22` | đọc `workspace.workspace_admin_actions` |
| `api/v1/admin/billing/workspaces/{id}` | `billing/.../AdminWorkspaceAnalyticsController.cs:24-25` | analytics |
| `api/v1/admin/global-glossary` | `transcript/.../GlobalGlossariesController.cs` | glossary toàn hệ thống |
| `api/v1/admin/notifications` | `notification/.../NotificationsController.cs` | broadcast |

**Gateway là allow-list, và allow-list bị test khoá.** `AdminRouteExposureTests.cs:12-19` liệt kê đúng 5 path được phép; test fail nếu gateway expose thêm hoặc bớt. Route mới của ticket này phải thêm vào **cả** `gateway appsettings.json` **và** file test đó.

*(Ghi chú lệch chuẩn, không thuộc scope: `WorkspaceOutboxAdminController.cs:15-16` dùng `[Authorize(Roles = "Admin")]` — role workspace chữ hoa, không phải `SystemAdminAuthorization`. Route là `api/v1/workspaces/outbox` nên không nằm trong allow-list gateway. Đáng mở ticket riêng.)*

**Web**: `warptalk-web/src/app/(app)/admin/` có `page.tsx` (overview), `billing/`, `global-glossary/`, `workspaces/[workspaceId]/`. Gate ở `admin/layout.tsx:7-10` qua hook `useIsSystemAdmin`. Nav ở `linear-sidebar.tsx:736-787` dưới nhóm "Platform".

⇒ Baseline: system admin **quan sát** được tenant và **quản lý** glossary/notification, nhưng **không cấu hình được policy nào chi phối hành vi của mọi workspace**.

---

## Scope

Mỗi dòng dưới đây là một policy chi phối **mọi** workspace, đang cố định lúc compile.

| # | Policy | Hardcode ở đâu (đã verify) | Bảng đề xuất | Ai đọc |
|---|---|---|---|---|
| P-1 | Danh sách public email domain | `EmailAddress.cs:9-14`; web `email-domain.ts:1-15`; web `settings/page.tsx:352` (bản trôi) | `platform.system_configurations` key `platform.public_email_domains`, `value` = JSON array | BE: `WorkspaceService.cs:97,153,414`; `VerifiedDomainService.cs:64`; `WorkspaceInvitationPolicy.cs:63,136`. FE: `workspace/create/page.tsx:82`, `workspace/page.tsx:41`, `settings/page.tsx:352` |
| P-2 | Trial workspace member limit (`5`) | `WorkspaceConstants.cs:19`; số 5 lặp trong message `:111` | `platform.system_configurations` key `platform.trial_workspace_member_limit` | `WorkspaceInvitationService.cs:953`; `WorkspaceInvitationAcceptanceProcessor.cs:182` |
| P-3 | Ép `AllowExternalLlm = true` | `WorkspaceConfiguration.cs:94` | `platform.feature_flags` key `platform.allow_workspace_opt_out_external_llm` | `WorkspaceDirectoryService.cs:239`; `DocumentSecurityGuardrailConsumerService.cs:302`; `WorkspaceGrpcService.cs:198` |
| P-4 | Trần cấu hình tenant: max active rooms `50`, artifact retention `3650` ngày, invitation expiry `365` ngày | `WorkspaceConstants.cs:11,13,18` | `platform.service_configurations` (`service_name='workspace'`) | `WorkspaceSettingsValidator.cs:29,35,41`; `WorkspaceConfiguration.cs:58`; `WorkspaceInvitationMapper.cs:19` |
| P-5 | Mặc định workspace mới: language `"en"`, timezone `"UTC"`, max rooms `5`, retention `30`, invite expiry `7` | `WorkspaceConstants.cs:6-9,16`, áp qua field initializer `WorkspaceConfiguration.cs:8-13` | `platform.service_configurations` (`service_name='workspace'`) | mọi `new WorkspaceConfiguration()` |
| P-6 | Grace period mặc định cho external (`24`h) | `DocumentAccessEvaluator.cs:276`, fallback của key appsettings `WorkspaceConstants.cs:139` | `platform.service_configurations` | `DocumentAccessEvaluator.cs:276` |
| P-7 | Văn bản consent cho `self_asserted` domain (D-12 của plan 163) | **chưa có trong code** — plan dự định hardcode string ở FE | `privacy.policy_versions`, `policy_type = 'verified_domain_assertion'` | Advanced settings dialog (plan 163 §4.5) |

### Min bounds — **giữ nguyên trong code, không phải policy**

`MinWorkspaceMaxActiveRooms = 1`, `MinWorkspaceArtifactRetentionDays = 1`, `MinWorkspaceInvitationExpiryDays = 1` (`WorkspaceConstants.cs:10,12,17`). Cả ba đều là "không âm, không zero" — sanity check, không phải quyết định kinh doanh. Không có system admin nào muốn đặt retention tối thiểu là 5 ngày; nếu muốn thì đó là **trần dưới của chính sách**, một khái niệm khác. Cho vào config chỉ thêm bề mặt sai mà không thêm khả năng nào.

`MaxWorkspaceNameLength = 150` (`WorkspaceConstants.cs:22`) — comment ngay trên nó (`:21`) ghi rõ nó mirror cột `workspace.workspaces.name VARCHAR(150)`. **Tuyệt đối không cấu hình được**: đặt 300 thì DB reject, đặt 50 thì dữ liệu cũ đọc ra vi phạm chính validator của mình. Đây là ràng buộc schema, phải đi cùng migration.

### Lý do P-4 là policy còn Min thì không

Trần quyết định **tenant được phép chọn tới đâu**. `retention 3650` là trần chi phí + compliance; `invitation expiry 365` là trần bảo mật của token; `max active rooms 50` là trần dung lượng. Đổi một trong ba là đổi hành vi của mọi tenant → đúng định nghĩa platform policy. Min chỉ chặn giá trị vô nghĩa → validation.

*(`max active rooms` có tranh chấp với entitlement layer — xem OQ-2.)*

---

## Out of scope

- **Admin workspace monitoring dashboard.** `AdminWorkspacesController.cs`, `IAdminWorkspaceService.cs:15-30` (`GetDirectoryAsync` / `GetDetailAsync` / `SuspendAsync` / `ReactivateAsync`), web `admin/workspaces/`. Ticket này cấu hình policy, không quan sát tenant. Không đụng gì tới các file đó.
- **Toàn bộ workspace-level verified-domain work** — thuộc `plan.md` (spec 163). Cụ thể không re-scope: D-4 (JSON là mirror), D-7 (Owner/Admin chọn membership type), D-8/D-14 (Owner-only), D-10 (`require_verified_domain_for_internal` là **giá trị dẫn xuất**), D-15/RC-7 (bất biến một-domain-một-workspace).
- **`RequireVerifiedDomainForInternal` không bao giờ là platform config.** Plan D-10 (`plan.md:292`) chốt nó là giá trị dẫn xuất từ số verified domain active. Đưa nó vào `platform.*` là tái lập WT-179 ở tầng cao hơn.
- **Chuẩn hoá `verification_method`** (`"trusted"` ở `VerifiedDomainMapper.cs:31` vs `"system"` ở `WorkspaceMapper.cs:123`) — plan item 3.5 (`plan.md:706`) đã nhận. Xem OQ-7 cho một lỗ nhỏ trong cách plan mô tả nó.
- **DNS TXT verification thật** — WT-157.
- **Refactor `EmailAddress.PublicDomains` ra sau interface** — plan item 3.12 (`plan.md:712`) đã nhận. Ticket này **chỉ thay implementation**, không đụng call site. Xem OQ-8 về thứ tự merge.
- **Global glossary / notification admin** — đã có.
- **Billing entitlement / quota** — hệ thống riêng (migration `050-05-08-2026-add-entitlement-layer.sql`).
- **`platform.audit_logs`, `activity_logs`, `outbox_events`, `inbox_events`, `service_health_checks`, `service_deployments`, `supported_languages`** — cũng dead nhưng không phải policy management.

---

## Proposed approach

### 0. Quyết ownership trước (chặn mọi thứ khác)

`platform` không nằm trong logical database nào (`extract-logical-databases.sh:160-168`). Trước khi viết một dòng code: chọn service sở hữu, thêm schema vào `extract-logical-databases.sh`, thêm role vào bảng ownership ở `check-database-boundaries.sh:182-200`, và viết migration trong `scripts/service-migrations/<service>/`. Xem OQ-1.

### 1. Write path — system admin

- `POST/PUT /api/v1/admin/platform-config` + `GET`, gate bằng `[Authorize(Policy = SystemAdminAuthorization.PolicyName)]` (`SystemAdminAuthorization.cs:16`).
- Thêm `/api/v1/admin/platform-config/{**catch-all}` vào gateway `appsettings.json` **và** `AdminRouteExposureTests.cs:12-19`.
- Mỗi lần ghi, trong **cùng transaction**: update `system_configurations` / `feature_flags` / `service_configurations` (giữ `updated_by`, `updated_at`), và INSERT một row `config_change_logs` với `config_scope` (`system` | `feature_flag` | `service:<name>`), `config_key`, `old_value`, `new_value`, `changed_by`, `change_reason`. `change_reason` **bắt buộc** ở API — audit không có lý do thì chỉ là log.
- Validate trước khi ghi: mỗi key có schema riêng (P-1 = array of hostname; P-2 = int ≥ 1; P-4 = int trong khoảng an toàn). Không nhận JSON tuỳ ý.

### 2. Read path — trong service

Không bao giờ query DB per-call. P-1 nằm trên đường create-workspace (`WorkspaceService.cs:97`) và đường invite (`WorkspaceInvitationPolicy.cs:63,136`).

Ba tầng:

1. **Snapshot bất biến trong process** (`IMemoryCache` hoặc một `volatile` record). Đây là thứ mọi call site đọc — chi phí bằng `HashSet.Contains` hiện tại.
2. **Nạp lúc startup**, và refresh theo TTL ngắn (60s) làm lưới an toàn nếu pub/sub trượt.
3. **Fallback hardcode**: nếu DB/Redis chưa sẵn sàng lúc boot, dùng đúng danh sách đang có ở `EmailAddress.cs:9-14`. Service **không được** fail-open (coi mọi domain là non-public) và cũng không được crash.

### 3. Invalidation xuyên service — copy pattern đã chạy production

Đã có sẵn tiền lệ đúng hình dạng: `EntitlementsChangedConsumer` (`workspace/.../BackgroundServices/EntitlementsChangedConsumer.cs:34`) subscribe Redis pub/sub channel `warptalk:entitlements:changed` (`WarpTalk.Shared/Events/EventEnvelope.cs:157`), giữ snapshot local, và degrade an toàn. Comment ở `:18-33` ghi lại bài học đắt: exception thoát khỏi `ExecuteAsync` của một `BackgroundService` **giết cả process** — WarpTalk đã ship sự cố đó hai lần. Nên bắt buộc:

- subscribe trong retry loop có backoff (`:54-66`),
- mỗi message handler bọc `try/catch` riêng,
- worker chết không được kéo service chết.

Thiết kế: channel `warptalk:platform:config-changed`, payload là `{ scope, key }` (không phải giá trị — consumer tự đọc lại, tránh gửi giá trị `is_sensitive` qua pub/sub). Hằng số channel đặt cạnh `EntitlementsChangedChannel` trong `WarpTalk.Shared/Events/EventEnvelope.cs`.

Hạ tầng Redis đã sẵn: `IDistributedCache` + `IConnectionMultiplexer` đăng ký ở `workspace/.../Infrastructure/DependencyInjection.cs:146-163`.

**Lưu ý về cache hiện có**: `WorkspaceCacheService` (`Infrastructure/Caching/WorkspaceCacheService.cs:9-46`) là Redis-backed nhưng **không dùng lại được**: nó chỉ cache active workspace của từng user, prefix `active_workspace:` (`:12`), TTL 7 ngày (`:27`), và `IWorkspaceCacheService` (`:7-11`) **không có method nào để invalidate** — chỉ `Set` và `Get`. Platform config cần một component riêng.

### 4. Ai tiêu thụ policy nào

| Policy | Service tiêu thụ | Cách nhận |
|---|---|---|
| P-1 public domains | Workspace (6 call site) | snapshot + pub/sub |
| P-1 public domains | Web | endpoint đọc (đã authenticated — cả `workspace/create` lẫn `settings` đều sau login), thay 2 bản sao FE. Xem OQ-4 |
| P-2 trial limit | Workspace | snapshot |
| P-3 external LLM | Workspace; giá trị đi tiếp sang Transcript/AI qua `WorkspaceGrpcService.cs:198` | snapshot |
| P-4/P-5/P-6 bounds & defaults | Workspace | snapshot |
| P-7 consent text | Web (qua Workspace API) | đọc `policy_versions` theo `is_active` |

Chưa service nào khác cần P-1 hôm nay (grep `"gmail.com"` toàn bộ `*.cs` chỉ ra `EmailAddress.cs` và test) — nhưng đó chính là lý do đưa nó ra khỏi Domain layer của Workspace trước khi service thứ hai copy nó.

---

## Acceptance criteria

- [ ] Schema `platform` được khai báo thuộc đúng một logical database: có mặt trong `extract-logical-databases.sh` và trong bảng ownership của `check-database-boundaries.sh`; `check-database-boundaries.sh` pass.
- [ ] Có migration trong `scripts/service-migrations/<owner>/` tạo `system_configurations`, `feature_flags`, `service_configurations`, `config_change_logs` trong database của service sở hữu, **cột giống hệt** `init-db.sql:1361-1416` (không thêm cột mới).
- [ ] Migration seed `platform.public_email_domains` bằng đúng 13 giá trị hiện có ở `EmailAddress.cs:9-14`.
- [ ] `GET /api/v1/admin/platform-config` trả 200 cho caller có role claim `"admin"`, 403 cho role `"Admin"` (workspace admin), 401 cho anonymous.
- [ ] `AdminRouteExposureTests.OnlyApprovedAdminRoutesAreExposed` pass sau khi thêm route mới vào cả gateway config lẫn allow-list.
- [ ] Mỗi lần ghi platform config sinh **đúng một** row `config_change_logs` với `old_value` ≠ `new_value`, `changed_by` = caller, `change_reason` non-null; ghi thất bại thì config **không** đổi (cùng transaction).
- [ ] `PUT` không kèm `change_reason` → `400`.
- [ ] Thêm `fastmail.com` qua admin UI → invite `x@fastmail.com` làm Internal bị từ chối, và `POST verified-domains` với `fastmail.com` trả `CannotVerifyPublicDomain` — **không restart service nào**. Độ trễ ≤ 5 giây.
- [ ] Xoá `fastmail.com` khỏi config → hai hành vi trên đảo lại, cũng không restart.
- [ ] Đường create-workspace và đường invite **không phát sinh query DB nào** tới `platform.*` per request (đo bằng integration test đếm command, hoặc EF interceptor).
- [ ] Redis down lúc service boot → service vẫn start, dùng fallback list, log warning; không crash. Redis down khi đang chạy → service giữ snapshot cuối, không denial.
- [ ] Worker pub/sub ném exception → service **không** dừng (regression test cho bài học ở `EntitlementsChangedConsumer.cs:18-33`).
- [ ] `settings/page.tsx:352` và `email-domain.ts:1-15` không còn chứa danh sách literal; cả hai đọc từ một nguồn. Grep `"gmail.com"` trong `warptalk-web/src` (trừ test) không ra kết quả.
- [ ] Đổi P-5 (ví dụ default timezone → `Asia/Ho_Chi_Minh`) → workspace tạo **sau** đó nhận giá trị mới; workspace tạo **trước** đó không đổi.
- [ ] `MaxWorkspaceNameLength` (`WorkspaceConstants.cs:22`) và ba hằng `Min*` vẫn là `const` trong code — có test khẳng định chúng không nằm trong platform config.
- [ ] `is_sensitive = true` → giá trị không xuất hiện trong response `GET` (masked) và không xuất hiện trong `config_change_logs.old_value/new_value`. *(phụ thuộc OQ-5)*

---

## Open questions

**OQ-1 — Service nào sở hữu schema `platform`?** *(chặn toàn bộ ticket)*
`platform` không nằm trong logical database nào (`extract-logical-databases.sh:160-168`, `check-database-boundaries.sh:182-200`). Ba lựa chọn, không cái nào rõ ràng thắng:
(a) Workspace service sở hữu — rẻ nhất, nhưng đặt platform policy vào một bounded context tenant-level, đúng thứ ticket này đang chê;
(b) Auth service sở hữu — nó đã giữ `auth.roles` seed role `'admin'` (`SystemAdminAuthorization.cs:19-22`), gần "platform" nhất trong số hiện có;
(c) service `platform` mới — sạch nhất, đắt nhất, và có lẽ quá sức capstone.
**Không tự quyết được từ code.**

**OQ-2 — `max active rooms` là platform config hay billing entitlement?**
`WorkspaceConstants.cs:11` đặt trần 50 cứng, trong khi entitlement layer đã tồn tại (`migrations/050-05-08-2026-add-entitlement-layer.sql`, `EntitlementsChangedConsumer`). Nếu quota phòng thuộc gói thuê bao thì P-4 phần "max rooms" thuộc billing, không thuộc ticket này — và ticket này chỉ giữ hai trần còn lại. Chưa đọc đủ billing để kết luận.

**OQ-3 — Các bảng `platform.*` có tồn tại vật lý ở môi trường nào hôm nay không?**
`init-db.sql` tạo chúng trong database `warptalk` dùng chung. Sau khi tách logical database, không rõ database dùng chung đó còn được deploy hay chỉ còn là legacy (`migrations/LEGACY-ONLY.txt` gợi ý có phân biệt nhưng chưa đọc). Ảnh hưởng trực tiếp tới việc migration là `CREATE TABLE` hay `ALTER`.

**OQ-4 — Web nhận platform config bằng cách nào?**
Runtime endpoint (chính xác, thêm một round-trip trên create page) hay build-time generate từ config (đơn giản, nhưng "config không cần deploy" chỉ đúng nửa vời vì FE vẫn phải build lại)? Ràng buộc: `workspace/create/page.tsx:82` chạy sau login nên endpoint authenticated là đủ, không cần public endpoint.

**OQ-5 — `is_sensitive` nghĩa là gì?** Cột có ở `system_configurations` (`init-db.sql:1366`) và `service_configurations` (`:1397`) nhưng không có comment, không có code, không có tiền lệ. Mask trong response? Loại khỏi audit log? Cần một quyết định trước khi acceptance criteria cuối cùng có nghĩa.

**OQ-6 — Có dùng `feature_flags` không, hay chỉ `system_configurations`?**
`feature_flags` có `rollout_percentage` và `conditions` (`init-db.sql:1381-1382`). Rollout theo phần trăm cho một policy an ninh (P-3 external LLM) là ý tồi — nó tạo ra hai nhóm tenant có mức bảo vệ khác nhau một cách ngẫu nhiên. Đề xuất: chỉ dùng `is_enabled`, để `rollout_percentage = 0`, và ghi rõ trong ticket rằng hai cột kia chưa được hỗ trợ. Cần xác nhận.

**OQ-7 — Ai sửa `verification_method` "trusted" vs "system"?**
Đã verify cả hai: `VerifiedDomainMapper.cs:31` ghi `"trusted"`, `WorkspaceMapper.cs:123` có default param `verificationMethod = "system"`. Không có `COMMENT` nào trên cột `workspace_verified_domains.verification_method` (`init-db.sql:234`) nên không tồn tại từ vựng chuẩn ở tầng DB. Plan item 3.5 (`plan.md:706`) **chỉ nói tới `WorkspaceMapper.cs:123`**; `plan.md:274-275` có nhắc cả hai và bảo "gộp vào 3.5", nhưng bảng công việc thì không liệt kê `VerifiedDomainMapper.cs:31`. Rủi ro: sửa xong vẫn còn một giá trị mồ côi. *(Cũng lệch nhỏ: `plan.md:418` mô tả `verification_token` "hiện là Guid ngẫu nhiên" — đúng với `WorkspaceMapper.cs:133`, nhưng `VerifiedDomainMapper.cs:32` ghi literal `"N/A"`.)* → nên confirm với owner của plan 163, không tự sửa ở ticket này.

**OQ-8 — Thứ tự merge với branch 163.**
`IPublicEmailDomainProvider` **chưa tồn tại** — grep toàn repo chỉ ra `plan.md`, và `plan.md:5` ghi trạng thái "chờ duyệt, chưa sửa code". Ticket này giả định seam đó đã có (D-13). Nếu branch 163 không merge trước, ticket này phải tự tạo interface + chuyển 6 call site, tức nuốt luôn item 3.12 và làm PR to hơn đáng kể. Cần chốt: chờ 163, hay tách item 3.12 ra merge sớm?

**OQ-9 — Audit ở đâu?** Ba ứng viên cùng tồn tại: `platform.config_change_logs` (đúng ngữ nghĩa nhất, đang trống), `platform.audit_logs` (`init-db.sql:1418`, cũng dead), và `workspace.workspace_admin_actions` (đang **live**, có UI đọc qua `AdminAuditLogController.cs:21`). Đề xuất của ticket là `config_change_logs`, nhưng như vậy admin phải nhìn hai màn audit khác nhau. Chấp nhận, hay hợp nhất?

---

## Estimated breakdown

| # | Sub-task | Ghi chú |
|---|---|---|
| 1 | **Chốt OQ-1** + migration tạo 4 bảng trong DB của service sở hữu; cập nhật `extract-logical-databases.sh` và `check-database-boundaries.sh` | chặn tất cả; ~0.5 ngày nếu OQ-1 đã có câu trả lời |
| 2 | Entity + DbContext mapping + repository cho 4 bảng | scaffold thuần |
| 3 | `IPlatformConfigProvider` + snapshot in-process + nạp lúc startup + TTL refresh + fallback hardcode | phần đúng-sai nhiều nhất |
| 4 | Redis pub/sub publisher + consumer theo pattern `EntitlementsChangedConsumer` (retry loop, per-message catch, không kéo process chết) | copy pattern, nhưng phải có regression test |
| 5 | Admin API `GET`/`PUT` + validate per-key + ghi `config_change_logs` cùng transaction + `change_reason` bắt buộc | |
| 6 | Gateway route + cập nhật `AdminRouteExposureTests.cs:12-19` | nhỏ, dễ quên |
| 7 | Thay implementation `IPublicEmailDomainProvider` (P-1) — **không đụng call site** | phụ thuộc OQ-8 |
| 8 | Chuyển P-2, P-4, P-5, P-6 sang provider | cơ học nhưng chạm `WorkspaceSettingsValidator`, `WorkspaceConfiguration` |
| 9 | P-3: bỏ `value with { AllowExternalLlm = true }` ở `WorkspaceConfiguration.cs:94`, thay bằng feature flag | cần quyết định migration cho workspace đang có `false` trong JSON |
| 10 | Web: trang `admin/platform-config` + nav item ở `linear-sidebar.tsx:736-787` | |
| 11 | Web: xoá 2 bản sao danh sách (`email-domain.ts:1-15`, `settings/page.tsx:352`), đọc từ nguồn duy nhất | đây là fix cho drift đã tồn tại |
| 12 | P-7: `policy_versions` + `policy_type = 'verified_domain_assertion'` | chỉ làm nếu plan 163 D-12 đã merge |
| 13 | Test: integration (không query per-call, Redis down, worker crash), authorization (`"admin"` vs `"Admin"`), audit row | |

Item 12 có thể cắt ra ticket riêng nếu 163 chưa xong. Item 9 có thể cắt riêng — nó là thay đổi hành vi thật, khác với 7/8 vốn no-op.
