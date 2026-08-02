# PR #70 Remaining Review Quality Gate Report

**PR:** `WarpTalk-CapstoneProject/warptalk-backend#70`
**Reviewer:** `huynhthaitu124`
**Scope chot:** cac blocker con lai sau grill-me, uu tien recycle bug va new bug co test cover.
**Source of truth:** voi transaction API, dong bo theo `transcript/`, khong theo `meeting/`.

## 1. Scope Chot Sau Grill-me

| Ma | Quyet dinh | Trang thai can dat |
|---|---|---|
| RB1 Transaction API | Lay `transcript/` lam source of truth. Khong them lai transaction API kieu `meeting/` vao Workspace `IUnitOfWork`. | Khong doi DB/interface ngoai cac thay doi can thiet cho blocker khac. |
| RB2 Role-change outbox | Enqueue role-change outbox truoc `SaveChangesAsync`; payload phai la `MemberRoleChangedEventPayload`. | Test persistence/payload pass. |
| RB3 Duplicate `UserId` trong workspace member | Out of scope theo user confirmation. | Khong sua trong scope nay. |
| RB4 Workspace member status casing | Dong bo style domain/storage theo transcript: canonical lowercase string, doc legacy read compatible. | New writes lowercase; query active case-insensitive. |
| RB5 VerifiedDomains omitted | Dung separate PATCH DTO nullable va merge tai controller/API boundary. | Omit giu nguyen; explicit empty list van la action ro rang va qua guard. |
| RB6 Artifact retention | Min la `1`, khong cho `0`. Validate BE va FE. | Constants, validator, domain settings, UI schema/input/copy deu min 1. |
| RB7 VerifiedDomains strict mode | Neu `RequireVerifiedDomainForInternal=true`, `VerifiedDomains` bat buoc not-null/not-empty. Neu owner off strict mode, UI disable field domain nhung khong xoa data ngam. | BE validator va FE controls cung enforce. |
| RB8 Role preview signing key | `WorkspaceMemberService` khong fail constructor vi missing key; preview/apply lazy-fail co error ro. | Constructor require `IConfiguration` non-null; missing key chi anh huong preview/apply. |
| RB9 PII embedding fallback | Neu `PiiDetected=true`, khong bao gio fallback raw `FullText`; chi index masked text khi mask non-empty. | Empty/null/whitespace mask skip embedding. |
| RB10 FE role-change contract | Web hook/service goi dung preview/apply API, khong dung `any` fallback che loi contract. | Typecheck pass. |
| RB11 PR metadata/deploy | Out of code scope. | Khong deploy lai; PR body/title xu ly rieng neu can. |
| RB12 Durable idempotency/single-use token | Defer, khong claim guarantee durable. | Khong implement persistence store trong scope nay. |

## 2. Recycle Bug Gates

| Recycle bug | Gate bat buoc |
|---|---|
| Omit `VerifiedDomains` bi hieu thanh clear all. | `WorkspaceSettingsPatchRequest` nullable; controller test PATCH omit giu `VerifiedDomains`. |
| Explicit empty `VerifiedDomains` bi block sai khi strict mode off. | Controller test explicit empty success khi `RequireVerifiedDomainForInternal=false`. |
| Workspace member status quay lai PascalCase. | Mapper writes `active`/`removed`; repository active query case-insensitive; migration normalize existing rows. |
| Retention min 0 quay lai. | BE tests reject 0; FE contract test assert min 1/input min 1/copy 1-3650. |
| Missing preview signing key lam hong unrelated endpoints. | Service constructor non-null config; preview/apply lazy key resolution; tests cover constructor/key behavior. |
| PII empty mask fallback raw text. | E2E tests cover masked-only publish va empty/null/whitespace mask skip. |
| FE role-change API drift. | Typed request/response DTOs, service methods, hooks, `npm run typecheck`. |

## 3. New Bug Gates

| New bug co the tao | Gate |
|---|---|
| PATCH nested AI policy overwrite field khac. | Patch mapper merge field-by-field. |
| Strict verified domain lam tat ca settings update fail khi strict off. | Validator chi require domains khi strict true. |
| FE owner off strict mode mat domains dang co. | Controls disabled theo strict mode; form khong auto-clear `verifiedDomains`. |
| Existing PascalCase DB rows bien mat khoi active query. | Repository filter lower-case comparison va SQL migration normalize. |
| New `resend` route/type lam web typecheck fail. | `resend` khai bao trong `package.json`; typecheck pass. |

## 4. Verification Commands

- `dotnet build .\workspace\src\WarpTalk.WorkspaceService.API\WarpTalk.WorkspaceService.API.csproj --no-restore`
- `dotnet test .\workspace\tests\WarpTalk.WorkspaceService.Tests\WarpTalk.WorkspaceService.Tests.csproj --no-restore`
- `npm run test:settings-validation`
- `npm run typecheck`
- Static grep:
  - no `WorkspaceMemberStatus.*.ToString()` for active/removed/suspended writes;
  - no retention min 0/copy 0 in settings page;
  - no conflict markers in touched frontend files;
  - no raw `FullText` fallback when `PiiDetected=true`.

## 5. Out Of Scope

- Duplicate `UserId`/workspace membership uniqueness follow-up.
- Durable idempotency store va single-use preview token persistence.
- PR title/body update va re-review request tren GitHub.
- Production deploy.
