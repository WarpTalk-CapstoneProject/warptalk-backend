# Hotfix Plan: Report5 White-Box Testing Evidence Alignment

Date: 2026-08-15
Owner: WarpTalk testing/documentation hotfix
Requested by: User
Scope: Align Report5 Excel evidence with the real backend codebase for white-box testing, including unit tests, integration tests, and Postman/API evidence.

## 1. Problem Statement

Report5 currently has test workbooks with many passing rows, but the evidence is not strong enough because it is not consistently tied to the real backend code. The original workbook template must be preserved; fixes must extend or clone the template structure instead of deleting template sheets or restyling the workbook from scratch.

- `My Drive/Report5/Report5_Unit Test.xlsx` has 35 function sheets and function-level UTCIDs, but still contains template residue and sheet consistency issues.
- `My Drive/Report5/Report5_Test Report.xlsx` has 35 feature sheets and 227/227 raw final-round cases marked `Passed`, but it does not clearly separate Unit, Integration, System/API, and Acceptance evidence.
- Integration evidence is under-documented even though the backend has real `WebApplicationFactory` + `Testcontainers.PostgreSql` integration harnesses.
- Postman is mentioned as evidence, but the backend workspace currently has no Postman collection/Newman artifact. Existing `.http` files are mostly `weatherforecast` placeholders and cannot support a Postman automation claim.
- Some `Test Statistics` module labels still come from an old unrelated template, e.g. venue/advertisement/voucher/personality wording.

This hotfix plan does not implement the workbook edits yet. It defines the exact verification and update path so the final Report5 artifacts can honestly claim white-box evidence while keeping the original template shape.

## 2. SDD / Repository Context

The selected `sdd-workflow` skill expects `warptalk-backend/.speckit/memory/constitution.md`, but this checkout does not contain `.speckit`; it contains `.specify` and `specs/`. Therefore, the plan records this as a process mismatch and avoids inventing constitution compliance.

Observed backend evidence:

- Solution: `warptalk-backend/warptalk-backend.slnx`
- Test projects: 8 xUnit projects under `auth`, `workspace`, `translation-room`, `meeting`, `billing`, `notification`, `transcript`, and `gateway`
- Unit/contract tooling: `xunit`, `Microsoft.NET.Test.Sdk`, `Moq`, `NSubstitute`, `FluentAssertions`, `coverlet.collector`
- Integration tooling: `Microsoft.AspNetCore.Mvc.Testing`, `WebApplicationFactory`, `Testcontainers.PostgreSql`
- API inspection tooling: `Microsoft.AspNetCore.OpenApi`, Swagger/OpenAPI in several API projects
- Postman artifact status: not found in backend; must be created or Report5 must downgrade Postman wording to manual/API inspection only

## 3. Current Code Evidence Inventory

| Service | Test project | Test files | Integration files | Key evidence |
| --- | --- | ---: | ---: | --- |
| Auth | `auth/tests/WarpTalk.AuthService.Tests` | 29 | 2 | xUnit, NSubstitute, `WebApplicationFactory`, Testcontainers Postgres |
| Workspace | `workspace/tests/WarpTalk.WorkspaceService.Tests` | 54 | 6 | xUnit, NSubstitute, Testcontainers Postgres, mocked Redis/cache/gRPC |
| Translation Room | `translation-room/tests/WarpTalk.TranslationRoomService.Tests` | 67 | 6 | xUnit, Moq, FluentAssertions, Testcontainers Postgres, mocked Redis, test auth |
| Billing | `billing/tests/WarpTalk.BillingService.Tests` | 49 | 4 | xUnit, Moq, FluentAssertions, Testcontainers Postgres, SQL migration-backed integration tests |
| Gateway | `gateway/tests/WarpTalk.Gateway.Tests` | 30 | 0 | API/gateway contract tests, rate limit, JWT, SignalR/Redis subscriber behavior |
| Meeting | `meeting/tests/WarpTalk.MeetingService.Tests` | 27 | 0 | service/worker tests, LiveKit/egress/chat/poll/question coverage |
| Notification | `notification/tests/WarpTalk.NotificationService.Tests` | 21 | 0 | controller/service/validator/stream persistence tests |
| Transcript | `transcript/tests/WarpTalk.TranscriptService.Tests` | 12 | 0 | transcript access, corrections, glossary, polling policy tests |

Integration harnesses to cite in Report5:

- `auth/tests/WarpTalk.AuthService.Tests/Integration/BaseIntegrationTest.cs`
- `workspace/tests/WarpTalk.WorkspaceService.Tests/Integration/BaseIntegrationTest.cs`
- `billing/tests/WarpTalk.BillingService.Tests/Integration/BaseIntegrationTest.cs`
- `translation-room/tests/WarpTalk.TranslationRoomService.Tests/Integration/BaseIntegrationTest.cs`

API/Postman source candidates:

- Gateway route forwarding config: `gateway/src/WarpTalk.Gateway/appsettings.json`
- Controller route attributes across `*/src/*Service.API/Controllers`
- OpenAPI runtime endpoints where `AddOpenApi()` / `MapOpenApi()` are configured
- Existing `.http` files are placeholders and should not be used as final evidence without replacement

Original template references to preserve:

- Unit template workbook: `Report - old version/Report5_Unit Test.xlsx`
  - Template/control sheets: `Guideline`, `Cover`, `Functions`, `Statistics`
  - Function template sheets: `Function 1`, `Function 2`, `Function3`, `Example`
  - Function template pattern: metadata rows 2-7, UTCIDs on row 9, data block from row 10, freeze pane `A10` for function sheets
- Test Report template workbook: `Report - old version/Report5_Test Report.xlsx`
  - Template/control sheets: `Cover`, `Test Cases`, `Test Statistics`
  - Feature template sheets: `Feature 1`, `Feature 2`
  - Feature template pattern: metadata rows 2-8, headers on row 10, function group on row 11, cases from row 12, freeze pane `A11`

## 4. Target Outcome

After the hotfix, Report5 should support these claims:

1. Unit testing is backed by real xUnit test files and function-level Unit workbook rows.
2. Integration testing is backed by real `WebApplicationFactory` + `Testcontainers.PostgreSql` harnesses and named integration test classes.
3. API/Postman testing is backed by an actual Postman collection or downgraded to Swagger/manual API inspection if no collection is created.
4. Report5 traceability links `FE-01` through `FE-07`, `F001` through `F035`, actual test files, test level, test type, tool, and evidence notes.
5. Statistics and pass-rate formulas are internally consistent and free of unrelated template labels.

## 5. Work Plan

### Phase 0 - Freeze Inputs and Back Up Deliverables

Tasks:

- Copy current workbook files into a timestamped backup folder before editing:
  - `My Drive/Report5/Report5_Unit Test.xlsx`
  - `My Drive/Report5/Report5_Test Report.xlsx`
  - latest `Report5_Test Documentation*.docx`
- Record SHA-256 hashes and last modified timestamps.
- Confirm the official source workbook names with the team before editing.

Exit criteria:

- Backup folder exists.
- Hash manifest exists.
- No workbook edits have started before backup.

### Phase 1 - Build the Code-to-Test Inventory

Tasks:

- Generate a machine-readable inventory of test projects:
  - test project path
  - package references
  - test class files
  - integration test files
  - controller/API route files covered by tests
- Classify tests into:
  - `Unit`
  - `Integration`
  - `Contract`
  - `API/System`
  - `Worker/Redis/SignalR`
  - `Security/NFR`
- Mark dependency strategy:
  - pure mock/substitute
  - `WebApplicationFactory`
  - `Testcontainers.PostgreSql`
  - mocked Redis/gRPC/external provider
  - real SQL migration script

Code to read:

- All `*Tests.csproj`
- All files under `*/tests/*/Integration`
- Representative unit/service/controller tests for each `F001-F035`
- Gateway `appsettings.json` route forwarding
- API controllers under `*/src/*Service.API/Controllers`

Exit criteria:

- A CSV/Markdown inventory can answer: "Which real test file proves this Report5 row?"
- Every `F001-F035` has at least one candidate actual test file or an explicit gap.

### Phase 2 - Fix Unit Workbook Evidence Without Breaking the Original Template

Tasks:

- Preserve original template/control sheets such as `Guideline`, `Cover`, `Functions`, and `Statistics`.
- Do not delete `Guideline` or any sheet needed to explain the official FLM/testcase template.
- If template examples need to remain available, keep them as visible template sheets or move them only after explicit approval; default is to keep them.
- Normalize every function sheet to one consistent layout:
  - `Function Code`
  - `Function Name`
  - `Created By`
  - `Executed By`
  - `Test Requirement`
  - UTCID row
  - `Passed`, `Failed`, `Untested`
  - `N/A/B`
  - `Total Test Cases`
- Repair known sheet issues:
  - `F007_CreateWorkspace` has shifted header/UTCID rows compared with the other sheets.
  - Several sheets have blank `Total Test Cases` cells even though UTCIDs and pass counts exist.
- Use `Report - old version/Report5_Unit Test.xlsx` as the visual/layout source of truth when repairing function sheets.
- Recalculate Unit totals from actual UTCIDs, not manually typed summary cells.
- Add or update a `Code Evidence` column in `Statistics` and `Traceability Matrix`.

Required white-box source validation:

- Read the actual test files named in each unit workbook row.
- Verify the function name in the workbook maps to a real class/method/test subject.
- If a function is covered by controller or integration tests rather than pure unit tests, label it honestly as `Contract/API` or `Integration-supported`, not pure `Unit`.

Exit criteria:

- Unit workbook has no template/guideline residue.
- Each `F001-F035` has real test file evidence or a documented gap.
- Unit totals equal actual UTCID count and pass/fail/untested counts.

### Phase 3 - Fix Integration Testing Evidence

Tasks:

- Add a dedicated `Integration Evidence` sheet to `Report5_Test Report.xlsx` or add columns to existing sheets:
  - `Test Level`
  - `Integration Harness`
  - `Database/External Dependency`
  - `Mocked Dependency`
  - `Actual Test File`
  - `Evidence Note`
- Map real integration folders:
  - Auth integration tests
  - Workspace integration tests
  - Billing integration tests
  - Translation Room integration tests
- Explicitly document what is mocked:
  - Workspace mocks Redis/cache/gRPC identity
  - Translation Room mocks Redis and workspace meeting policy
  - Auth mocks workspace invitation client
  - Billing uses Testcontainers Postgres and selected SQL migrations
- Do not claim full cross-service production integration where the test harness stubs another service.

Exit criteria:

- Integration rows distinguish real DB integration from mocked external-service boundaries.
- Report5 can explain why Testcontainers evidence is white-box/integration evidence.
- No Integration claim relies only on `Passed` text without a test file reference.

### Phase 4 - Create or Correct Postman/API Evidence

Decision:

- If the team needs a Postman claim, create a real Postman collection and run artifact.
- If time is short, remove/downgrade Postman automation wording and use Swagger/OpenAPI/manual API inspection only.

Recommended Postman creation path:

- Generate a collection from OpenAPI where available.
- Fill missing endpoints by reading controller route attributes and Gateway route config.
- Cover minimum smoke flows:
  - Auth login/register/profile/settings
  - Workspace create/select/invite/accept/member role/document happy path
  - Translation room create/join/end/artifact read path
  - Meeting poll/question/chat path
  - Billing plans/subscription/credits/payment checkout/webhook signature negative case
  - Notification list/read/admin notification path
  - Transcript by-room/export/correction/glossary path
- Add environment variables:
  - `gatewayBaseUrl`
  - `authBaseUrl`
  - `workspaceId`
  - `translationRoomId`
  - `meetingId`
  - `accessToken`
  - test user IDs/emails
- Add pre-request or setup requests for token capture.
- Run with Newman if available and save:
  - collection JSON
  - environment JSON
  - run summary
  - failed request details

Exit criteria:

- Either a real Postman/Newman evidence package exists, or Report5 no longer claims Postman automation.
- Postman rows cite collection path and run date.
- `.http` placeholder files are not treated as final evidence.

### Phase 5 - Fix Test Report Workbook Statistics Without Changing the Template Layout

Tasks:

- Replace old template module labels in `Test Statistics` with WarpTalk labels:
  - Authentication
  - Voice Consent
  - Password Management
  - Profile Management
  - User Settings
  - Workspace Invitation
  - Workspace Management
  - Workspace Domain
  - Workspace Join Request
  - Workspace Member Role
  - Room Participant
  - Translation Room Lifecycle
  - Translation Room Resume
  - Voice Clone Consent
  - Translation Room Join
  - Poll Management
  - Meeting Lifecycle
  - Meeting Join
  - Poll Voting
  - Question List
  - Billing Authorization
  - Credit Consumption
  - Webhook Handling
  - Usage Recording
  - Contract Terms
  - Messaging
  - Notification Read Status
  - Validation Test
  - Validation
  - Session Start
  - Workspace Suspension
  - Meeting Validation
  - Token Refresh
  - Assistant Q&A
  - Workspace Knowledge
- Repair formulas:
  - The original `Feature 1` / `Feature 2` template counts all rounds from Round 1 column `F`. Preserve the row/column layout, but correct the formulas so Round 2 counts use column `I` and Round 3 counts use column `L`.
  - Round 2 failed/pending/N/A counts must read Round 2 column, not Round 1.
  - Round 3 failed/pending/N/A counts must read Round 3 column, not Round 1.
  - pass rate must be `Passed / Total Test Cases`.
- Add `Test Level` and `Test Type` to all summary rows.
- Add `Actual Test File` and `Tool Evidence` to all rows.
- Preserve `Cover`, `Test Cases`, `Test Statistics`, and feature sheet row/column structure from `Report - old version/Report5_Test Report.xlsx`.

Exit criteria:

- No stale template module names remain.
- No `#REF!`, `#DIV/0!`, `#VALUE!`, `#NAME?`, or broken formula text remains.
- Workbook-level total equals the sum of sheet-level cases.

### Phase 6 - Update Traceability

Tasks:

- Expand traceability from `F001-F035` to include:
  - `FE-01` through `FE-07`
  - FR/NFR where available
  - Report5 workbook sheet
  - Unit workbook sheet
  - Actual test file
  - Test level
  - Test type
  - Evidence tool
  - Evidence confidence: `Strong`, `Partial`, `Missing`
- Mark NFR rows honestly:
  - Security: partial/strong depending on auth, RBAC, directory isolation, audit tests
  - Performance: missing unless latency evidence is added
  - Usability: missing unless UI/browser evidence is added
  - Maintainability: supported by architecture and test suite evidence, not by pass counts alone

Exit criteria:

- Every Report5 testcase row can be traced to a feature/function and evidence source.
- Missing evidence is visible rather than hidden by `Passed`.

### Phase 7 - Verification

Required checks:

- Run workbook scanner:
  - sheet count
  - status counts
  - formula error scan
  - stale-template keyword scan
  - traceability completeness scan
- Run backend test commands, time permitting:
  - `dotnet test warptalk-backend/warptalk-backend.slnx`
  - targeted project tests if full solution is too slow
- If Docker is available, run integration test projects that use Testcontainers:
  - Auth
  - Workspace
  - Billing
  - Translation Room
- If Postman collection is created:
  - run Newman
  - export report
  - update Report5 evidence path and status

Exit criteria:

- Workbook totals and formulas are correct.
- Code evidence exists for all strong claims.
- Remaining gaps are explicitly marked as `Missing` or `Out of Scope`.

## 6. Acceptance Criteria for This Hotfix

The hotfix is complete when:

- `Report5_Unit Test.xlsx` has no deliverable-visible template residue and has consistent function sheet totals.
- Original template sheets and workbook layout are preserved unless the user explicitly approves hiding/removing examples.
- `Report5_Test Report.xlsx` has no stale unrelated module names.
- Integration evidence is backed by real test files and test harness descriptions.
- Postman is either backed by a real collection/run artifact or removed as an automation claim.
- Traceability includes real code/test file references.
- Failed/Pending/N/A counts are formula-correct for all rounds.
- Any missing System/Acceptance/NFR evidence is labeled honestly rather than hidden.

## 7. Regression Risks

- Editing workbook formulas may break existing charts or summary figures used in DOCX.
- Adding new columns may affect screenshot layout in Report5 documentation.
- Over-claiming Postman/Newman without a real run artifact creates reviewer risk.
- Running all integration tests requires Docker; local machine state may block Testcontainers.
- Some API routes may be gateway-only and not directly runnable against service-local ports.

## 8. Recommended Execution Order

1. Backup workbooks.
2. Generate code/test inventory.
3. Fix Unit workbook layout and totals.
4. Fix Test Report statistics labels and formulas.
5. Add Integration Evidence and Traceability columns.
6. Decide Postman: create real collection or downgrade claim.
7. Update DOCX screenshots/tables after workbook stabilizes.
8. Run final workbook and code-evidence verification.

## 9. Open Decisions

- Should Postman be required as automated evidence, or is Swagger/manual API evidence acceptable for this submission?
- Should Acceptance/UAT/OAT be added to this same hotfix, or handled as a separate Report5 acceptance-evidence hotfix?
- Which workbook is the source of truth if Google Drive sync creates duplicate Report5 files?
- Are FE-03.5 Voice Cloning and FE-07.4 Audit Logging now ready to mark as covered, or should they remain explicit gaps?
