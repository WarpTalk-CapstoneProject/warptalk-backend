# Ke hoach xu ly blocker review PR #70

**PR:** `WarpTalk-CapstoneProject/warptalk-backend#70`  
**Branch:** `chore/update-auto-save-settings-pages` -> `development`  
**Reviewer:** `huynhthaitu124`  
**Trang thai:** Ban nhap ke hoach xu ly blocker  
**Ngay:** 2026-08-02

## 1. Muc tieu

Xu ly cac blocker rui ro cao duoc neu trong review PR #70 truoc khi merge. Cac thay doi phai giu Workspace Service la nguon su that cho membership, settings, verified-domain policy va document guardrail decisions.

Ke hoach nay tap trung vao cac blocker co the lam hong hanh vi production, lam lo du lieu nhay cam, hoac mo ta sai cac dam bao security:

- outbox row cua role-change khong duoc persist;
- role-change event dang dung sai payload schema;
- tai lieu co PII co the fallback sang raw text khi dua vao embedding;
- thieu cau hinh role-preview signing co the lam hong tat ca member endpoints;
- `VerifiedDomains` bi omit co the bi hieu la xoa toan bo domain;
- preview/idempotency dang duoc the hien nhu guarantee nhung chua enforce day du;
- viec mo rong quyen verified-domain cho Admin can quyet dinh product ro rang.

## 2. Decision Log theo kieu Grill-me

Day la cac cau hoi thiet ke quyet dinh huong implement. Cau tra loi khuyen nghi duoc xem la huong resolver mac dinh neu product owner khong override.

| Cau hoi | Cau tra loi khuyen nghi | Ly do |
|---|---|---|
| Co nen save outbox event bang cach goi them `SaveChangesAsync` sau khi publish? | Khong. Enqueue role-change outbox message truoc cung mot lan `SaveChangesAsync` commit membership changes. | Giu member update va outbox row atomic trong mot EF unit of work. Goi save lan hai co the commit role state nhung mat event neu lan save sau fail. |
| `workspace.member.role_changed` co nen tam dung lai `MemberRemovedEventPayload`? | Khong. Tao payload rieng `MemberRoleChangedEventPayload`. | Event name va payload schema phai khop nhau. Consumer khong nen phai suy luan role-change tu schema member-removed. |
| PII detection co nen block toan bo indexing? | Khong nhat thiet. Masked indexing chap nhan duoc chi khi co masked content khong rong. | Policy "mask roi index" co the hop ly, nhung raw PII tuyet doi khong duoc gui vao embedding. |
| Co nen resolve role-preview signing key trong constructor service? | Khong. Chi resolve lazy trong preview/apply operation, hoac dung options validator rieng cho role-change feature. | List/remove/update member khong nen fail vi mot feature preview thieu config. |
| Route cu `PUT /members/{userId}/role` co nen giu lai? | Co, nhu compatibility adapter, nhung phai enforce cung Owner-only va target guardrails. Khong claim route nay co preview/idempotency protection. | Tranh break client ngay lap tuc trong khi van dong duoc bypass security. |
| Co nen claim idempotency la durable trong v1? | Khong, tru khi them persistent idempotency store. Xem key la correlation metadata va document ro retry behavior. | Khong co storage thi khong the dedupe replay. Mo ta dung guarantee quan trong hon mot guarantee gia. |
| Admin co duoc manage verified domains khong? | Mac dinh la Owner-only cho den khi product approve Admin access ro rang. | Verified domains dinh nghia bien gioi Internal membership, nen day la privilege expansion. |
| Omit `VerifiedDomains` co nghia la "clear all domains" khong? | Khong. Omit la giu nguyen; empty list ro rang moi la remove all, va van phai qua guard. | Auto-save va payload kieu PATCH khong duoc mutate field khac mot cach pha huy. |
| `PATCH /settings` hien co co nen tiep tuc nhan raw `JsonObject` roi deserialize ve `WorkspaceSettingsDto` khong? | Khong. Them DTO rieng `WorkspaceSettingsPatchRequest` voi nullable properties va merge method ro rang. | Full DTO hien co co cac field non-nullable, nen controller test phai chung minh API phan biet duoc omitted field va explicit empty value. |

## 3. Resolver B1: Persist Role-change Outbox Events

### Van de

`WorkspaceOutboxWriter.EnqueueAsync` chi add `WorkspaceOutboxMessage` vao DbContext hien tai, khong save. Trong `ChangeMemberRoleCoreAsync` va `TransferOwnershipAsync`, PR #70 enqueue role-change event sau `SaveChangesAsync`, nen outbox row khong duoc commit.

### Resolver

Chuyen cac loi goi `PublishMemberRoleChangedAsync` len truoc `SaveChangesAsync` trong moi service path phat outbox-backed event:

- normal role change;
- demotion event cua owner cu khi transfer ownership;
- promotion event cua owner moi khi transfer ownership.

Giu mot lan `SaveChangesAsync` cuoi cung cho ca state changes va outbox rows.

### Buoc implement

1. Trong `WorkspaceMemberService.ChangeMemberRoleCoreAsync`, set `targetMember.RoleId`, update repository, enqueue `PublishMemberRoleChangedAsync`, roi moi goi `SaveChangesAsync`.
2. Trong `TransferOwnershipAsync`, update `workspace.OwnerId`, role cua previous owner va new owner, enqueue ca hai role-change events, roi goi `SaveChangesAsync`.
3. Giu pattern cua `RemoveMemberAsync` neu da enqueue truoc khi save.
4. Them integration-style test kiem tra persistence cua `WorkspaceOutboxMessage`, khong chi assert mocked publisher call.

### Test bat buoc

- role change persist dung mot `WorkspaceOutboxMessage` voi type `MemberRoleChanged`;
- ownership transfer persist dung hai role-change outbox rows;
- khi `SaveChangesAsync` fail, khong commit role state va khong commit outbox message;
- publisher mock tests van giu, nhung khong phai coverage duy nhat.

### Acceptance Gate

Co database-backed test chung minh committed outbox chua role-change event sau role mutation thanh cong.

## 4. Resolver B2: Thay Payload Sai Cua Role-change

### Van de

`OutboxWorkspaceEventPublisher.PublishMemberRoleChangedAsync` tao event ten `workspace.member.role_changed` nhung serialize `MemberRemovedEventPayload`. Viec nay lam mat `oldRole`, `newRole`, `membershipType`, `effectiveBehavior`, `eventId` va `idempotencyKey`.

### Resolver

Tao payload contract rieng cho role-change va serialize no duoi role-change event type.

Payload khuyen nghi:

```csharp
public sealed record MemberRoleChangedEventPayload(
    string WorkspaceId,
    string TargetUserId,
    string OldRole,
    string NewRole,
    string ChangedByUserId,
    string MembershipType,
    string EffectiveBehavior,
    string EventId,
    string? CorrelationId,
    string? IdempotencyKey,
    DateTime EffectiveAt,
    DateTime OccurredAt);
```

### Buoc implement

1. Them `MemberRoleChangedEventPayload` canh cac workspace event payload contracts hien co.
2. Update `OutboxWorkspaceEventPublisher.PublishMemberRoleChangedAsync` de dung payload moi.
3. Update outbox delivery deserialization/switch logic de route `MemberRoleChangedEventPayload` dung cach.
4. Giu event name on dinh: `workspace.member.role_changed`.
5. Them serialization tests assert payload shape, khong chi event type.

### Test bat buoc

- role-change outbox JSON chua old role va new role;
- role-change outbox JSON khong deserialize nhu `MemberRemovedEventPayload`;
- correlation/idempotency values duoc preserve khi co;
- transfer ownership tao mot demotion payload va mot promotion payload.

### Acceptance Gate

Consumer nhan duoc schema mo ta ro role change va bao gom cac field service da nhan.

## 5. Resolver B3: Chan Raw PII Fallback Khi Embedding

### Van de

PR #70 cho phep indexing khi chi phat hien PII, bang cach dung masked content cho embedding. Policy nay chi chap nhan duoc neu masked text ton tai. Neu `PiiDetected == true` va `MaskedContent` null, empty hoac whitespace, flow hien tai co the fallback sang `content.FullText`, dua raw PII vao embedding pipeline.

### Resolver

Giu "PII co the mask roi index" chi khi co guard chat:

- neu `DlpDetected == true`, skip indexing;
- neu `PiiDetected == true` va masked content khong rong, index masked content;
- neu `PiiDetected == true` va masked content rong, skip indexing va mark document la skipped hoac failed theo ingestion semantics hien co;
- neu khong co violation, index full text.

### Buoc implement

1. Tao helper nho nhu `ResolveIndexingText(scanResult, fullText)` tra ve `(CanIndex, Text, Reason)`.
2. Khong tinh `textToIngest` theo kieu `MaskedContent ?? FullText` khi PII detected.
3. Audit/log branch "PII detected but masked content unavailable".
4. Lam ro trong PR documentation rang PII redaction nghia la "masked indexing", khong phai "block indexing", neu day la chu dich.

### Test bat buoc

- `PiiDetected=true`, `MaskedContent="masked"` publish embedding request voi masked text duy nhat;
- `PiiDetected=true`, `MaskedContent=null/empty/whitespace` khong publish embedding request;
- `DlpDetected=true` khong publish embedding request;
- clean scan publish embedding request voi full text;
- audit metadata ghi lai detection flags.

### Acceptance Gate

Khong co test path nao co the publish `content.FullText` khi `PiiDetected == true`.

## 6. Resolver B4: Tranh Constructor-time Failure Khi Thieu Preview Signing Key

### Van de

`WorkspaceMemberService` resolve role-preview signing key trong constructor. Neu `Security:RolePreviewSigningKey`, `WARPTALK_ROLE_PREVIEW_SIGNING_KEY` va `Jwt:Secret` hop le deu thieu, dependency injection fail va tat ca member endpoints co the tra 500, ke ca cac flow khong lien quan nhu list/remove/update.

### Resolver

Resolve signing key lazy chi trong preview/apply methods. Cac operation member khong lien quan khong duoc phu thuoc vao preview-token configuration.

Thu tu uu tien:

1. dedicated `Security:RolePreviewSigningKey`;
2. `WARPTALK_ROLE_PREVIEW_SIGNING_KEY`;
3. chi nhu fallback tam thoi, `Jwt:Secret` hop le.

Dong thoi bo sung config example cho local/dev deployments.

### Buoc implement

1. Thay `_previewSigningKey` field bang injected configuration reference hoac mot `IRolePreviewSigningKeyProvider` nho.
2. Chi goi provider tu `PreviewMemberRoleChangeAsync` va `ApplyMemberRoleChangeAsync`.
3. Tra ve controlled `ValidationError` hoac `ServiceUnavailable` style result khi preview signing chua duoc cau hinh.
4. Them `.env.example`, appsettings comments hoac deployment docs cho `WARPTALK_ROLE_PREVIEW_SIGNING_KEY`.
5. Bo optional constructor parameter leakage neu test co the inject configuration/provider cu the.

### Test bat buoc

- `ListMembersAsync` thanh cong khi thieu preview signing key;
- `RemoveMemberAsync` thanh cong/fail chi theo membership rules, khong theo signing config;
- preview/apply tra controlled error khi thieu signing key;
- preview/apply thanh cong khi dedicated key duoc cau hinh;
- placeholder keys nhu `CHANGE_ME` bi reject.

### Acceptance Gate

Thieu preview signing configuration chi co the lam hong preview/apply role-change, khong lam hong toan bo `WorkspaceMemberService`.

## 7. Resolver B5: Giu Nguyen Verified Domains Khi Bi Omit

### Van de

`UpdateWorkspaceSettingsAsync` tinh removed domains bang `settings.VerifiedDomains ?? new List<string>()`. Trong auto-save hoac partial settings payload, `VerifiedDomains` bi omit co the bi hieu thanh explicit empty list, gay fail update hoac accidental domain removal.

### Resolver

Tach ro full settings replacement va partial update semantics:

- voi `PUT` hien co, hoac yeu cau full payload va validate `VerifiedDomains` phai co;
- voi auto-save/partial updates, dung dedicated `PATCH` request DTO trong do moi field nullable va omitted fields duoc giu nguyen;
- khong bao gio xem omitted `VerifiedDomains` la "remove all".

Vi PR #70 them auto-save behavior, resolver khuyen nghi la thay raw `JsonObject` merge bang typed `WorkspaceSettingsPatchRequest`. Patch DTO phai giu duoc y nghia field co mat hay khong ngay tai API boundary, merge vao current `WorkspaceSettingsDto`, roi moi goi service hien co bang complete settings object.

### Buoc implement

1. Them `WorkspaceSettingsPatchRequest` voi nullable properties cho moi auto-save field, bao gom `List<string>? VerifiedDomains`.
2. Them mapper/helper nhu `ApplyPatch(currentSettings, patch)` tra ve full `WorkspaceSettingsDto`.
3. Update `PATCH /api/v1/workspaces/{id}/settings` de bind `WorkspaceSettingsPatchRequest`, khong dung raw `JsonObject`.
4. Giu `PUT /settings` la full replacement va validate complete payload semantics rieng.
5. O service-level removal detection, khong suy luan field presence tu full DTO. Neu removal logic can field presence, truyen command object co `VerifiedDomainsWasProvided`, hoac giu domain-removal checks o PATCH merge layer.
6. Remove dead check `!newDomainsSet.Contains(targetDomain)` khi `removedDomains` da la set difference.
7. Them tests cho omitted vs explicit empty domain list qua controller/API, khong chi service unit tests.

### Test bat buoc

- HTTP PATCH omit `verifiedDomains` giu current domains khong doi;
- HTTP PATCH voi `"verifiedDomains": []` co gang remove va bi block neu active internal members phu thuoc vao domain;
- HTTP PATCH voi `"verifiedDomains": []` thanh cong chi khi removal guards pass;
- HTTP PATCH update field khac khong call revoke-domain guard logic;
- HTTP PUT van la full replacement path va co full-payload test rieng;
- model binding khong bien omitted `verifiedDomains` thanh empty list truoc khi merge.

### Acceptance Gate

Payload auto-save cho field khong phai domain khong the remove domain va khong fail vi verified domains hien co.

## 8. Resolver B6: Can Chinh Preview Va Idempotency Theo Dung Guarantee

### Van de

Flow preview/apply moi co preview token, expiry, cooling-off va idempotency fields, nhung:

- legacy `PUT /members/{userId}/role` co the bypass preview/apply;
- idempotency key chi duoc validate non-empty;
- preview token co the replay trong cua so validity.

### Resolver

Mo ta ro v1 guarantee.

Khuyen nghi v1:

- Owner-only authorization va target-role guardrails ap dung tren ca legacy route va route moi;
- preview token chi cung cap freshness cho apply route moi;
- idempotency key la correlation metadata, khong phai durable replay dedupe;
- durable idempotency va single-use token defer den khi approve persistence store.

### Buoc implement

1. Giu legacy route nhu compatibility adapter voi Owner-only, no self-change, no Owner target, no External target.
2. Khong document legacy route nhu duoc bao ve boi preview/cooling-off.
3. Update DTO/API docs de label idempotency key la correlation metadata neu chua them store that.
4. Tao follow-up issue/spec neu durable idempotency bat buoc truoc merge.
5. Can nhac tra `409 RoleChangeStale` khi replay sau apply thanh cong lan dau.

### Test bat buoc

- Admin khong the dung legacy route de change roles;
- Owner khong the dung bat ky route nao cho self-change, Owner target hoac External target;
- route moi reject stale preview sau khi target role da thay doi;
- replay sau successful apply khong emit duplicate events khi state da duoc doi;
- docs/tests khong claim durable dedupe neu chua implement.

### Acceptance Gate

Khong con route nao bypass mandatory role-change authorization guardrails.

## 9. Resolver B7: Chot Boundary Quyen Verified-domain

### Van de

PR #70 mo rong add/revoke verified-domain tu Owner-only sang Owner-or-Admin. Verified domains anh huong viec user co duoc xem la Internal hay khong, nen day la authorization boundary change.

### Resolver

Mac dinh Owner-only tru khi product owner approve Admin management ro rang.

Neu Admin access duoc approve, update tat ca naming va documentation:

- rename `OnlyOwnerCanManageDomains`;
- update error messages;
- restore XML/API docs giai thich business rule;
- ghi privilege expansion vao PR title/body.

### Buoc implement

1. Kiem tra `141-workspace-members/spec.md` va workspace policy docs lien quan de xac nhan wording Owner/Admin.
2. Neu khong co approval ro, revert Add/Revoke guards ve `IsOwner()`.
3. Giu List la Owner/Admin neu Settings visibility duoc thiet ke cho Admin.
4. Restore comment ve no-DNS-challenge/business-rule trong service/API docs.
5. Them tests cho Owner, Admin, Member va External behavior.

### Test bat buoc

- Owner co the add/revoke verified domains;
- Admin khong the add/revoke tru khi product decision noi nguoc lai;
- Admin co the list chi khi settings access van la Owner/Admin;
- Member/External khong the list/add/revoke;
- revoke co active internal members bi block.

### Acceptance Gate

Verified-domain mutation permissions khop product policy da document va test names/error constants khong con mau thuan voi behavior.

## 10. Resolver B8: Update PR Metadata

### Van de

PR title/body dang trinh bay mot thay doi lon ve feature/security nhu mot chore va body rong. Reviewer khong the danh gia an toan authorization, PII va API changes tu metadata.

### Resolver

Retitle va document PR truoc khi request re-review.

Title khuyen nghi:

```text
feat(workspace): add auto-save settings and role governance safeguards
```

PR body toi thieu:

- tom tat role preview/apply behavior va compatibility route behavior;
- neu ro Owner-only role changes;
- neu ro verified-domain permission decision;
- neu ro PII masked-indexing policy;
- liet ke config requirement cho role preview signing;
- liet ke tests duoc them cho tung blocker.

### Acceptance Gate

Reviewer co the hieu tat ca behavior/security/data-policy changes tu PR body ma khong can doc full diff truoc.

## 11. Thu Tu Xu Ly Khuyen Nghi

1. Sua role-change outbox persistence.
2. Them correct role-change payload va delivery support.
3. Patch PII indexing guard de chan raw-text fallback.
4. Chuyen preview signing key sang lazy va document config.
5. Sua semantics khi omit `VerifiedDomains`.
6. Can chinh legacy route, preview va idempotency guarantees.
7. Chot quyet dinh Owner/Admin cho verified-domain.
8. Them hoac update tests cho moi resolver.
9. Retitle va dien PR body.
10. Request re-review tu `huynhthaitu124`.

## 12. Recycle Bug Control Matrix

Day la cac cach bug da bi review co the quay lai duoi hinh dang khac. Moi resolver phai co guard test tuong ung.

| Recycle bug | Resolver guard | Proof bat buoc |
|---|---|---|
| Role-change event van khong persist vi mot role path khac enqueue sau save. | Centralize role-change mutation qua mot core method va enqueue truoc single commit. | Tests cover normal role change va transfer ownership. |
| Event da persist nhung van mang removed-member schema. | Them `MemberRoleChangedEventPayload` ro rang va assert serialized JSON fields. | Serialization test fail neu payload type regress. |
| PII path van fallback raw text qua helper/adapter khac. | Dua indexing text resolution ve mot helper duy nhat va test moi combination cua scan result. | Mock embedding publisher capture text va chung minh raw text khong xuat hien khi PII detected. |
| Missing signing key van lam hong unrelated member endpoints. | Bo constructor-time resolution va test service methods khi missing config. | `ListMembersAsync`/`RemoveMemberAsync` chay duoc khi khong co preview key. |
| Bug omit `VerifiedDomains` song sot vi model binding tao empty list. | Dung typed nullable PATCH DTO va controller-level tests. | HTTP PATCH omit case chung minh domains giu nguyen. |
| Legacy route van la preview/idempotency bypass. | Route legacy calls qua cung guardrail core va document lower guarantees. | Admin/self/Owner/External target tests cover ca hai routes. |
| Admin verified-domain privilege quay lai qua mot endpoint. | Test add, revoke va list rieng theo role. | Admin mutation tests fail tru khi product approve privilege expansion ro rang. |

## 13. New Bug Prevention Matrix

Day la cac bug ma chinh ban fix co the tao ra. Tat ca phai duoc cover truoc khi request re-review.

| Potential new bug | Cach phong tranh | Test bat buoc |
|---|---|---|
| Duplicate role-change outbox rows khi stale apply/replay. | Tra `409 RoleChangeStale` hoac no-op truoc enqueue khi current role da khac preview old role. | Replay sau successful apply khong enqueue event thu hai. |
| RabbitMQ/outbox delivery khong deserialize duoc payload moi. | Update delivery switch/deserializer cung commit voi payload. | Outbox delivery test route duoc `MemberRoleChangedEventPayload`. |
| Consumer break vi event version/name doi. | Giu event name on dinh va chi them payload fields, khong rename event. | Contract test assert `workspace.member.role_changed`. |
| PII-with-empty-mask bi mark sai ingestion status tren UI. | Chot va document mot status: khuyen nghi `skipped` cho policy skip, `failed` chi cho processing failure. | Test assert status da chon va lifecycle event. |
| Lazy signing che mat deploy misconfiguration. | Log specific error va tra specific error code cho preview/apply. | Missing-key preview/apply tests assert error code/message. |
| PATCH vo tinh overwrite nested AI policy fields khac. | Merge nested objects field-by-field, khong replace toan bo `AiUsagePolicy` neu chi gui mot field con. | PATCH mot nested DLP field van preserve redaction/profile/glossary values. |
| PUT va PATCH semantics lech nhau am tham. | Document PUT la full replace va PATCH la partial merge. | Test rieng cho PUT full replacement va PATCH partial merge. |
| Owner-only verified-domain mutation lam Admin UI hien action bi 403. | Backend tests cong voi frontend/API contract note bat buoc Admin UI hide mutation controls. | Controller tests chung minh 403; frontend follow-up hide actions neu nam trong scope. |

## 14. Verification Checklist

- `dotnet test` cho Workspace tests pass.
- Role-change persistence co DB-backed outbox test.
- Role-change payload JSON chua schema dung.
- PII tests chung minh raw text khong bao gio duoc index sau PII detection.
- Member list/remove/update endpoints khong can preview signing config.
- Auto-save settings payload dung typed PATCH DTO va khong mutate omitted domain fields.
- Legacy role-change route khong bypass Owner-only authorization.
- Verified-domain mutation policy duoc document va test.
- PR description liet ke tat ca behavior/security changes.

## 15. Follow-up Defer

Khong nen nhet cac muc nay vao blocker fix neu chua duoc approve ro:

- durable role-change idempotency store;
- single-use preview token voi nonce/jti persistence;
- role-change audit/history endpoint;
- shared helper cho domain-in-use checks giua settings update va verified-domain revoke;
- bounded concurrency hoac database-backed search cho `ListMembersAsync`;
- transaction API policy cho `IUnitOfWork` sau khi outbox pattern duoc chot.
