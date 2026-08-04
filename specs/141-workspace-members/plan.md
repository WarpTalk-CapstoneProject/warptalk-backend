# Implementation Plan: Workspace Policy, Settings Toggle & Owner-only Role Governance

**Feature:** WT-141 Workspace Member Management, extended by approved WT-157/WT-158 workspace policy boundaries  
**Status:** In Progress — direct `WorkspaceMember` persistence; downstream consumers require follow-up specs  
**Primary service:** Workspace Service  
**Affected clients/services:** `warptalk-web`, Gateway, Translation Room, Transcript, Notification and document-ingestion workers

## 1. Scope and architectural constraints

Workspace Service remains authoritative for workspace configuration, membership type and role. Auth remains the identity/token authority and does not own workspace policy decisions. Existing gRPC/event boundaries, `WorkspaceService`, `WorkspaceMemberService`, `IWorkspaceEventPublisher`, Gateway signed context and Notification Service abstractions are extended rather than replaced. No cross-schema database joins or direct HTTP service calls are introduced.

The existing `WorkspaceConfiguration` JSONB model remains the persistence boundary. `141-workspace-members/spec.md` is approved with Owner-only role changes and invitation rules.

### Mandatory module boundary

This plan is scoped to Workspace settings and Workspace member governance. Implementation may modify only:

- `warptalk-backend/workspace` source/tests/specs;
- `warptalk-web` workspace routes, hooks, services, types and workspace page documentation;
- existing Workspace-owned event publisher code where required to emit a compatible event.

Do not independently edit Auth, Gateway, Notification, Transcript, Translation Room, Meeting, Billing, AI workers or shared contracts as part of this plan. Downstream services may be inspected and their existing contracts tested from the Workspace boundary, but consumer implementation changes must be captured in a separate approved spec/plan owned by that module. If Notification delivery or cache freshness cannot work with an existing contract, record it as a dependency/blocker and do not workaround it by changing another module.

## 2. Required behavior decisions

### Member and role governance

- Only the active Workspace Owner can change `Admin ↔ Member`.
- Owner transfer remains a separate Advanced/Transfer Ownership command; normal role change cannot target Owner or self.
- Target must be active and Internal. External membership is immutable and always remains `Member`; remove/leave and invite again is the only reclassification path.
- Admin may invite only Member. Only Owner may invite Admin, using the same promotion review/cooling-off safeguards.
- `CanCreateMeetings` is independent of role. Role changes never mutate it; the preview displays its current value and the existing separate capability action manages it.
- New authorization applies to subsequent API requests, workspace selection/login/reconnect and new meeting joins/sessions. An active meeting keeps its admission/session snapshot and is not interrupted.

### Owner confirmation UX

- `/[workspaceSlug]/members` remains a directory. Internal Members see read-only minimal data; management actions are not rendered for them.
- Add Owner-only `/[workspaceSlug]/settings/access-management` for role governance, impact preview and the latest operation receipt.
- No inline or bulk role mutation.
- Flow: select target → server preview → show grants/revocations, `CanCreateMeetings` and timing → type target identity → confirm.
- `Member → Admin` has a 60-second cooling-off period, a second explicit confirmation and 15-minute expiry; it is never auto-applied.
- `Admin → Member` applies after review confirmation without delay.
- v1 has no password/Google step-up auth. Typed confirmation, Owner-only authorization and stateless preview-token/current-role freshness checks are required. No membership-version column, role-change store, or new Workspace SQL table is introduced. The apply request carries an idempotency key as correlation metadata; durable replay deduplication and historical role-audit listing require a separately approved persistence change.
- Only target receives in-app and email notification. No broadcast and no Owner email; Owner receives UI receipt/audit.

## 3. Phase 0 — Spec, contracts and tests first

1. Update `141-workspace-members/spec.md` with Owner-only role changes, External immutability, invitation constraints, new-session semantics and notification behavior; mark it Approved after review.
2. Add acceptance criteria for the workspace-settings toggle matrix and downstream propagation, referencing WT-157 and WT-158.
3. Define contracts before implementation: role preview/apply, `WorkspaceMemberRoleChanged.v1`, notification payload, preview-token freshness and stale-change errors.
4. Write failing tests first for authorization, External immutability, preview/apply concurrency, cooling-off state, post-commit Rabbit event delivery/metrics, target-only notification, settings field authorization and downstream consumer contracts.

## 4. Phase 1 — Domain and application layer

### Role-change model

- Keep the existing `WorkspaceMember` persistence model unchanged; derive preview freshness from a signed stateless token and re-read the target role before apply.
- Sign preview tokens with the configured Workspace/JWT signing secret (never a persisted workspace value); deployments must provide a stable secret so preview/apply can cross instances.
- Add typed preview/apply command/result models containing target role, idempotency key, preview hash/token, effective behavior and audit reference.
- Add typed errors: `OwnerOnly`, `ExternalRoleImmutable`, `RoleChangeStale`, `InvalidTargetRole`, `SelfRoleChange` and `CoolingOffNotComplete`.
- Refactor `ChangeMemberRoleAsync` so all paths use the Owner-only guardrail; preserve ownership transfer separately.
- Preserve `CanCreateMeetings` unchanged.
- Generate permission differences through the current capability/policy resolver, not hard-coded FE labels.
- Return an audit/event receipt containing actor, target, old/new role, membership type, timestamps, correlation/idempotency IDs and outcome. Do not claim durable history in Workspace without a persistence change; the access page displays the current operation receipt only.

### Workspace settings model

- Keep `WorkspaceConfiguration` JSONB as source of truth.
- Normalize FE/backend defaults, especially `RequireVerifiedDomainForInternal`.
- Expose/validate `UseGlobalGlossary` with default true when included in Owner policy UI.
- Add `EnforceHostApprovalDefault` mapping/UI if retained as supported policy.
- Preserve `AllowExternalLlm = true`; it is not an editable toggle.
- Add field-level update authorization so Admin payloads cannot overwrite Owner-only values.

## 5. Phase 2 — Infrastructure and event delivery

### Persistence and concurrency

- Do not add Workspace tables or columns and do not introduce a role-change store. Update the existing `WorkspaceMember.RoleId` directly on every successful role mutation.
- Use the existing Workspace unit-of-work/transaction boundary. Transfer Ownership must update `Workspace.OwnerId` and both member role IDs atomically; a normal Admin/Member change updates one membership row.
- Preserve the idempotency key in the command/event receipt. Without persistence, an ambiguous network retry is rejected as stale after the first role update rather than providing durable replay deduplication.
- Return `409 ROLE_CHANGE_STALE` when target state changed after preview.

### Workspace events

- Extend `IWorkspaceEventPublisher` and RabbitMQ implementation with `PublishMemberRoleChangedAsync`. The hybrid wrapper routes this event to RabbitMQ only; Redis is reserved for audio/realtime main-flow traffic and receives no role-change event.
- Publish `WorkspaceMemberRoleChanged.v1` containing event/change IDs, workspace/member/target IDs, old/new role, membership type, effective semantics, actor, correlation ID and idempotency key.
- Consumers are idempotent and ignore a stale role snapshot/event when their existing freshness mechanism supports it.
- Use the existing RabbitMQ event transport after the database transaction commits; do not add an outbox table/store or make synchronous HTTP calls from Workspace.

### Notification

- Verify the existing Notification consumer/adapter can consume the role-change event using existing persistence, realtime and email paths; if not, record a separate Notification-owned follow-up instead of editing that module here.
- Create one target notification only; never fan out to the workspace.
- Persist before realtime delivery and retry delivery failures. Notification failure never rolls back committed role/audit data.
- Use a dedicated notification type/template with old role, new role, effective timing and ongoing-meeting semantics; respect target preferences.

### Authorization freshness

- Invalidate/update target authorization cache when the role event is processed.
- Do not treat stale Gateway JWT role claims as authoritative for new requests; cache miss/version mismatch resolves current membership through Workspace gRPC.
- Keep active meeting authorization snapshots unchanged.

## 6. Phase 3 — API layer

Add versioned endpoints under the existing controller/service boundary:

```text
GET  /api/v1/workspaces/{workspaceId}/members/{userId}/role-change-preview?toRole=Admin|Member
POST /api/v1/workspaces/{workspaceId}/members/{userId}/role-change
```

Apply must carry target role, preview hash/token and idempotency key. Response returns updated member projection, effective semantics and an event/audit receipt; it must not remain an empty `204` for the new flow. No history endpoint is exposed until durable role-audit persistence is separately approved.

Keep the existing role endpoint as a guarded Owner-only compatibility adapter to the same command. No Admin-capable legacy path remains.

For settings, preserve GET/PUT compatibility, expose field-level/PATCH behavior for new policy updates, re-check actor role on every update and preserve Owner-only fields from Admin payloads.

## 7. Phase 4 — Frontend implementation

### Members page

- Keep directory access for Internal Members.
- Do not render management columns/actions for users without capability; do not rely only on disabled controls.
- Show External `Member · External · Fixed` and a non-action explanation.
- Keep `CanCreateMeetings` as separate Owner/Admin capability action.
- Add Owner-only `Manage access` link/action with target preselection.

### Access Management page

- Add route, Owner guard and direct-URL forbidden state.
- Load active Internal members and role counts; show the latest in-page operation receipt because durable role history is outside the no-schema-change scope.
- Implement server-backed preview, permission impact, typed confirmation, 60-second promotion countdown, 15-minute expiry and explicit final apply.
- Disable duplicate submits, never optimistic-update, and force refresh/re-preview after `409`.
- On success refresh member/workspace queries, navigation capabilities and receipt state.

### Workspace Settings page

- Retain current visual groups and form density.
- Add supported missing toggles only after backend/default tests exist: `EnforceHostApprovalDefault`, `UseGlobalGlossary`.
- Mark Owner-only fields read-only for Admin and enforce the same rule on backend.
- Do not claim a toggle is effective until its downstream consumer contract exists.

## 8. Phase 5 — Downstream policy verification

Add a consumer contract or explicitly mark unsupported for each setting:

- Invitation/Workspace: external collaboration, verified domains and internal enforcement.
- Meeting/Translation Room: allowed languages, max active rooms, host approval and `CanCreateMeetings`.
- Gateway: profanity filtering and signed workspace/role freshness.
- Transcript/Translation: translation profile and global glossary opt-out.
- Document security/ingestion: PII redaction and DLP blacklist, preserving approved-document/AI eligibility guards.
- Artifact/retention workers: retention days.
- Voice/audio pipeline: voice-cloning policy or explicit unsupported/fallback response.

Consumers use Workspace gRPC or versioned events and remain workspace-scoped; no cross-service DB reads.

## 9. Phase 6 — Verification and CI/CD

### Backend

- Owner success; Admin/Member/External `403` on role mutation.
- External, self, Owner target, invalid role, stale preview and duplicate submit rejection.
- `CanCreateMeetings` unchanged through both role directions.
- Database commit-before-event ordering, publisher failure metrics and notification failure without rollback.
- Event ordering/version, cache invalidation and new-request freshness.
- New meeting/join sees new role; active meeting keeps old snapshot.
- Settings field-level authorization and default normalization.

### Frontend and contracts

- Role/membership visibility matrix and Access Management route guard.
- Preview, typed confirmation, cooldown, expiry, stale refresh and External fixed state.
- Toggle round-trip and read-only Owner-only fields.
- `WorkspaceMemberRoleChanged.v1` compatibility.
- Target-only in-app/email delivery and retry.
- FE → API → persistence → gRPC/event → consumer integration for every supported toggle.

### Rollout

1. CI gates: FE lint/build, backend build, unit, integration, contract and migration validation.
2. Deploy any separately approved downstream consumers first with backward-compatible handling.
3. Deploy Workspace authorization/event changes.
4. Deploy FE Access Management/toggle UI behind a feature flag only for controls with a verified consumer.
5. Monitor role conflicts, cache staleness, event lag, notification failures and toggle propagation failures.
6. Keep the guarded compatibility adapter until all clients migrate; it must remain Owner-only throughout.

## 10. Documentation and completion gates

Update/create the page documentation required by `warptalk-web/AGENTS.md`:

- `warptalk-web/.agents/page-docs/workspace-members.md`
- `warptalk-web/.agents/page-docs/workspace-settings.md`
- `warptalk-web/.agents/page-docs/workspace-access-management.md`

Also update backend feature notes for role events, authorization freshness and downstream policy contracts. Implementation is complete only after `141-workspace-members/spec.md` is approved, Phase 0 tests precede production code, no Admin-capable legacy mutation remains and all supported toggles have a verified downstream consumer.

## 11. End-to-end verification matrix for Workspace Settings

Every control must be tested as one flow: initial GET/default → FE control state → API payload → Workspace persistence → read-after-write → gRPC/event projection → downstream behavior → failure/rollback UX. A successful `PUT` or `204` alone is not evidence that a setting works.

| Settings control | Workspace boundary | Downstream behavior to verify | E2E acceptance |
|---|---|---|---|
| Default Language | `WorkspaceConfiguration.DefaultLanguage` | Workspace selection/default room and translation fallback | Save, reload and create a room without language; configured default is used. |
| Timezone | `WorkspaceConfiguration.Timezone` | Workspace-scoped date/retention display where supported | Save/reload and verify consumers that claim to use timezone; otherwise classify as display-only. |
| Allowed Target Languages | `AllowedTargetLanguages` | Workspace gRPC `ValidateMeetingCreation` and Translation Room preflight | Allowed subset succeeds; unsupported language returns validation error. |
| Max Active Rooms | `MaxActiveRooms` | Translation Room active-room quota | Create up to the limit, reject the next active room, and verify ended rooms release quota. |
| Artifact Retention Days | `ArtifactRetentionDays` | Transcript/Summary finalization and retention cleanup | End meeting, verify `RetentionUntil`, expire artifact, run cleanup and verify storage/DB/audit. |
| Enforce Host Admission | `EnforceHostApprovalDefault` | Room creation/preflight and Translation Room admission | New rooms inherit setting; existing rooms retain explicit setting; waiting/approval behavior is verified. |
| Voice Cloning | `VoiceCloningEnabled` | Audio/translation voice pipeline | Observe actual voice consumer behavior; if none exists, mark the control unsupported/feature-flagged. |
| Profanity Filter | `IsProfanityFilterEnabled` | Gateway `AiResultConsumerService` | Toggle, publish AI result, verify filtering changes and cache/read behavior. |
| Allow External Collaboration | `AllowExternalCollaboration` | Invitation validation, join/preflight and external boundary | External invite is accepted/rejected according to new value; internal invitations remain valid. |
| Require Verified Domain | `RequireVerifiedDomainForInternal` | Invitation/acceptance, classification and domain revocation | Verify outside-domain invite, domain removal guard, existing-member behavior and default alignment. |
| Verified Domains | `VerifiedDomains` and verified-domain table | Invitation classification and verified-domain service | Add/duplicate/public/revoke flows, last-domain guard and invite classification are verified. |
| PII Redaction | `AiUsagePolicy.RedactPii.Enabled` | Document security scan/guardrail and AI ingestion | Upload/approve PII document and verify scan/redaction behavior. |
| DLP | `AiUsagePolicy.Dlp` | Security guardrail consumer | Blacklist detection, quarantine/rejection, empty list and toggle-off behavior are verified. |
| Translation Tone/Honorifics | `AiUsagePolicy.TranslationProfile` | Transcript/Translation prompt construction | Start transcript/translation and verify profile reaches prompt builder; otherwise classify as persistence-only. |
| Global Glossary | `AiUsagePolicy.UseGlobalGlossary` | Transcript `GlossaryStartedEventConsumer` | Toggle opt-out and verify global terms are/are not merged into STT/MT prompts; default is true. |
| Allow External LLM | normalized invariant | Document ingestion/embedding | False payload is normalized to true; document authorization/AI eligibility remains separate. This is not an Owner toggle. |

Also cover initial defaults, null/legacy JSON, invalid numeric values, duplicate domain/keyword, concurrent updates, unauthorized Admin field mutation, Member/External `403`, timeout, partial downstream outage, retry and FE query invalidation.

## 12. End-to-end verification matrix for Advanced Settings

Advanced is Owner-only and contains lifecycle operations, not ordinary policy toggles.

### Transfer Workspace Ownership

- FE candidates contain only active Internal members, exclude self/External, and handle empty or failed member queries.
- Preview identifies current Owner and resulting roles (`new Owner`, previous Owner → `Admin`).
- Backend atomically updates `Workspace.OwnerId` and both role IDs, preserves at least one active Owner and rejects stale/removed targets.
- Publish ownership/role events and invalidate both users' role/cache/navigation state.
- Previous Owner loses Owner-only Settings/Advanced access on the next request; new Owner gains it without interrupting active meetings.
- FE clears modal state, refreshes selection and redirects previous Owner only after success.
- Test direct API calls by Admin/Member, External target, removed target, concurrent transfer and event/notification failure.

### Delete Workspace

- FE requires exact workspace-name confirmation, disables duplicate submit and does not redirect on failure.
- Backend verifies active Owner and performs the existing soft-delete transaction; it must not silently become hard delete.
- Publish `WorkspaceDeleted` and verify Translation Room force-terminates active rooms, rejects new workspace requests and cleans/tombstones downstream resources according to service contracts.
- Verify documents, transcripts, summaries, billing/credits and notification references do not become orphaned or cross-tenant visible; asynchronous cleanup must be retryable and observable.
- On success invalidate workspace/member/document/settings caches, remove active context and redirect to workspace selection.
- Test Admin/Member/External denial, wrong confirmation, repeated delete, active-meeting termination, redelivery/idempotency and partial-cleanup retry.

## 13. What belongs in Workspace Settings versus downstream services

Workspace Settings contains policy intent/defaults, not service-specific operational knobs:

- tenant boundary: external collaboration, verified domains and internal-domain enforcement;
- meeting defaults: allowed languages, host admission, active-room quota and voice-cloning availability;
- data governance: artifact retention, PII/DLP, profanity and AI eligibility defaults;
- translation/knowledge defaults: translation profile and global glossary opt-out;
- workspace identity/localization: default language and timezone.

Downstream services consume these policies through gRPC/events:

- Translation Room owns room lifecycle, active-room counting, waiting/approval state and meeting-session snapshots;
- Transcript/Translation owns prompt construction, STT/MT language application, honorific/tone rules and glossary merging;
- Document/AI workers own scanning, PII/DLP enforcement, ingestion/index eligibility and embedding execution;
- Gateway owns signed context verification, cache freshness and profanity-result filtering;
- Notification owns inbox/email delivery, preferences, retry and delivery status;
- Billing owns subscription/credit limits and is not configured through general Settings JSONB;
- Auth owns identity, authentication and role catalog IDs, not workspace policy decisions.

Any policy without a real consumer contract must be labeled `Persisted only / not enforced` in the Settings API/UI until its E2E test passes.

## 14. E2E CI/CD gates

- Add a settings fixture that creates/selects a workspace, updates one field at a time, reads it back and exercises the consumer through gRPC/event boundaries.
- Add an Advanced fixture with Owner, Admin, Internal Member, External Member and one active meeting.
- Workspace-owned CI runs FE route/interaction tests, Workspace unit/integration tests, gRPC/event contract serialization and Workspace publisher tests. Notification/Gateway/Transcript/Translation Room consumer and delivery tests remain follow-up CI jobs owned by those modules; this plan must not edit them.
- Nightly/full pipeline covers retention cleanup, event redelivery, cache staleness and partial downstream outage.
- CD order remains shared contracts/consumers → Workspace backend → Notification/consumer workers → FE; feature flags prevent exposing a toggle before its consumer exists.

## 15. Open questions and approval gates

These are the remaining decisions that must be resolved before implementation starts:

1. **WT-141 spec approval:** resolved; `141-workspace-members/spec.md` is approved and aligned with Owner-only role/invitation rules.
2. **Unsupported consumer policy:** `VoiceCloning`, `TranslationProfile`, `Timezone` and any other setting without a verified downstream consumer must either remain explicitly labeled `Persisted only / not enforced` or be removed from the editable UI. No cross-module code may be added here to make them appear functional.
3. **Cross-module delivery dependency:** target email/in-app role notifications, role-cache invalidation and downstream meeting/session behavior require existing compatible event consumers. If the current contracts do not support them, create separate Notification/Gateway/meeting follow-up specs; this plan only emits and validates the Workspace-side contract.
4. **Role history/idempotency boundary:** durable role history and replay deduplication remain explicitly deferred because this plan cannot add tables/columns or a role-change store. The current flow returns a signed-preview/apply receipt and rejects post-commit retries as stale.

No other product behavior question remains open: Owner-only role mutation, External role immutability, independent `CanCreateMeetings`, 60-second promotion cooling-off, no v1 step-up auth, target-only notification, no notification rollback and active-meeting snapshot semantics are fixed decisions.
