# WT-141 Tasks: Workspace Policy, Settings Toggle và Owner-only Role Governance

## Phase 0 - Regression Signal (tests first)

- [x] Update role mutation tests to prove only an active Workspace Owner can mutate `Admin|Member` roles.
- [x] Add regression guard for Admin escalation and External role immutability.
- [x] Add backend field-level settings guard preventing Admin overwrite of Owner-only policy fields.
- [x] Add FE Owner-only Access Management route and invite-role visibility.

## Phase 1 - Workspace domain and application boundary

- [x] Keep the existing membership persistence model unchanged; use a signed stateless preview token instead of a membership-version field or role-change store.
- [x] Keep `CanCreateMeetings` independent from role mutation.
- [x] Preserve `UseGlobalGlossary` in settings DTOs and expose the control in Workspace Settings.

## Phase 2 - Workspace backend

- [x] Enforce Owner-only role mutation and External immutability in `WorkspaceMemberService` and controller.
- [x] Add Owner-only role-change preview endpoint; apply remains the guarded compatibility endpoint.
- [x] Update `WorkspaceMember.RoleId` directly for each role mutation; return an event/audit receipt and explicitly document that durable replay history/replay deduplication requires future persistence.
- [x] Apply field-level Owner/Admin settings authorization while retaining PUT compatibility.
- [x] Add Workspace-side `WorkspaceMemberRoleChanged.v1` RabbitMQ publisher contract; Redis is not used for this event.

## Phase 3 - Workspace web

- [x] Add Owner-only `/[workspaceSlug]/settings/access-management` flow with preview, typed confirmation and 60-second promotion cooldown.
- [x] Hide management actions from Internal Member; lock External role; keep `CanCreateMeetings` as a separate control.
- [x] Restrict Admin invitation role to Member and preserve Owner-only settings/advanced navigation.
- [x] Add `UseGlobalGlossary` control and read-after-write settings behavior.
- [x] Keep Advanced Transfer Ownership/Delete Workspace Owner-only with safe confirmation and refresh behavior.

## Phase 4 - E2E, CI/CD and documentation

- [x] Verify the Workspace-owned GET → FE → API → JSONB/RoleId persistence and gRPC/Rabbit contract paths; keep unsupported downstream controls explicitly persisted-only.
- [ ] Run downstream consumer E2E jobs for Translation Room, Transcript/Translation, Notification, Gateway and retention workers after their separately approved module specs land.
- [x] Run Workspace-owned lint/build/unit checks and record downstream follow-up jobs without editing other modules.
- [x] Update workspace members, settings and access-management page docs plus backend role-event/downstream contract notes.
- [x] Keep the legacy role endpoint only as a guarded Owner-only compatibility adapter; no Admin-capable legacy path remains.
