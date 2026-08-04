# PR #70 Review Blockers Resolution Plan

**PR:** `WarpTalk-CapstoneProject/warptalk-backend#70`  
**Branch:** `chore/update-auto-save-settings-pages` -> `development`  
**Reviewer:** `huynhthaitu124`  
**Status:** Draft plan for blocker resolution  
**Date:** 2026-08-02

## 1. Goal

Resolve the high-risk blockers raised in the PR #70 review before merge. The fixes must preserve Workspace Service as the source of truth for workspace membership, settings, verified-domain policy and document guardrail decisions.

This plan focuses on blockers that can break production behavior, leak sensitive data, or misrepresent security guarantees:

- role-change outbox rows are not persisted;
- role-change events use the wrong payload schema;
- PII-detected documents can fall back to raw text for embedding;
- missing role-preview signing configuration can break all member endpoints;
- omitted `VerifiedDomains` can be interpreted as removing every domain;
- preview/idempotency semantics are advertised but not fully enforced;
- verified-domain privilege widening needs an explicit product decision.

## 2. Grill-me Decision Log

These are the design questions that decide the implementation path. Recommended answers are treated as the resolver direction unless product owners override them.

| Question | Recommended answer | Why |
|---|---|---|
| Should outbox events be saved by calling `SaveChangesAsync` again after publishing? | No. Enqueue role-change outbox messages before the same `SaveChangesAsync` that commits membership changes. | Keeps member updates and outbox rows atomic in one EF unit of work. A trailing save can commit role state without an event if the second save fails. |
| Should `workspace.member.role_changed` reuse `MemberRemovedEventPayload` temporarily? | No. Add a dedicated `MemberRoleChangedEventPayload`. | Event names and payload schemas must match. Consumers cannot safely infer role changes from a removed-member schema. |
| Should PII detection block all indexing? | Not necessarily. Masked indexing is acceptable only when non-empty masked content exists. | The intended policy can be "mask then index", but raw PII must never be sent to embedding. |
| Should role-preview signing key be resolved in the service constructor? | No. Resolve lazily only for preview/apply operations, or register a dedicated options validator that fails only the role-change feature during startup checks. | Listing/removing/updating members should not fail because an optional preview feature lacks configuration. |
| Should the old `PUT /members/{userId}/role` route remain available? | Yes, as a compatibility adapter, but it must enforce the same Owner-only and target guardrails. Do not claim preview/idempotency protection on that route. | Avoids breaking clients immediately while removing the security bypass. |
| Should idempotency be claimed as durable in v1? | No, unless a persistent idempotency store is added. Treat the key as correlation metadata and document retry behavior. | Without storage, replay dedupe is impossible. Honest semantics beat fake guarantees. |
| Can Admin manage verified domains? | Default to Owner-only until product explicitly approves Admin access. | Verified domains define the Internal membership boundary, so this is a privilege expansion. |
| Should omitted `VerifiedDomains` mean "clear all domains"? | No. Omitted means unchanged; explicit empty list means remove all, subject to guards. | Auto-save and PATCH-like settings payloads must not destructively mutate unrelated fields. |
| Should the existing `PATCH /settings` keep accepting raw `JsonObject` and deserialize into `WorkspaceSettingsDto`? | No. Add a dedicated `WorkspaceSettingsPatchRequest` with nullable properties and an explicit merge method. | The current full DTO has non-nullable fields, so controller tests must prove the API can distinguish omitted fields from explicit empty values. |

## 3. Resolver B1: Persist Role-change Outbox Events

### Problem

`WorkspaceOutboxWriter.EnqueueAsync` adds `WorkspaceOutboxMessage` to the current DbContext but does not save it. In `ChangeMemberRoleCoreAsync` and `TransferOwnershipAsync`, PR #70 enqueues role-change events after `SaveChangesAsync`, so the outbox rows are never committed.

### Resolver

Move `PublishMemberRoleChangedAsync` calls before `SaveChangesAsync` in every service path that emits outbox-backed events:

- normal role change;
- ownership transfer demotion event for previous owner;
- ownership transfer promotion event for new owner.

Keep one final `SaveChangesAsync` for the state changes plus outbox rows.

### Implementation Steps

1. In `WorkspaceMemberService.ChangeMemberRoleCoreAsync`, set `targetMember.RoleId`, update repository, enqueue `PublishMemberRoleChangedAsync`, then call `SaveChangesAsync`.
2. In `TransferOwnershipAsync`, update `workspace.OwnerId`, previous owner role and new owner role, enqueue both role-change events, then call `SaveChangesAsync`.
3. Keep `RemoveMemberAsync` pattern unchanged if it already enqueues before saving.
4. Add an integration-style test that inspects `WorkspaceOutboxMessage` persistence, not only the mocked publisher call.

### Required Tests

- role change persists exactly one `WorkspaceOutboxMessage` with type `MemberRoleChanged`;
- ownership transfer persists exactly two role-change outbox rows;
- when `SaveChangesAsync` fails, no role state or outbox message is committed;
- publisher mock tests remain, but are not the only coverage.

### Acceptance Gate

A database-backed test proves the committed outbox contains the role-change event after a successful role mutation.

## 4. Resolver B2: Replace Wrong Role-change Payload

### Problem

`OutboxWorkspaceEventPublisher.PublishMemberRoleChangedAsync` currently creates an event named `workspace.member.role_changed` but serializes `MemberRemovedEventPayload`. This discards `oldRole`, `newRole`, `membershipType`, `effectiveBehavior`, `eventId` and `idempotencyKey`.

### Resolver

Create a dedicated role-change payload contract and serialize it under the role-change event type.

Recommended payload:

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

### Implementation Steps

1. Add `MemberRoleChangedEventPayload` next to existing workspace event payload contracts.
2. Update `OutboxWorkspaceEventPublisher.PublishMemberRoleChangedAsync` to use the new payload.
3. Update outbox delivery deserialization/switch logic to route `MemberRoleChangedEventPayload` correctly.
4. Keep event name stable: `workspace.member.role_changed`.
5. Add serialization tests that assert the payload shape, not only event type.

### Required Tests

- role-change outbox JSON contains old and new role;
- role-change outbox JSON does not deserialize as `MemberRemovedEventPayload`;
- correlation/idempotency values are preserved when provided;
- transfer ownership produces one demotion payload and one promotion payload.

### Acceptance Gate

Consumers receive a schema that explicitly represents a role change and includes the fields the service accepted.

## 5. Resolver B3: Prevent Raw PII Fallback During Embedding

### Problem

PR #70 permits indexing when only PII is detected, using masked content for embedding. That policy is acceptable only if masked text is available. If `PiiDetected == true` and `MaskedContent` is null, empty or whitespace, the current flow can fall back to `content.FullText`, sending raw PII to the embedding pipeline.

### Resolver

Keep "PII can be masked and indexed" only under a strict guard:

- if `DlpDetected == true`, skip indexing;
- if `PiiDetected == true` and masked content is non-empty, index masked content;
- if `PiiDetected == true` and masked content is empty, skip indexing and mark the document as skipped or failed according to existing ingestion semantics;
- if no violation exists, index full text.

### Implementation Steps

1. Introduce a small helper such as `ResolveIndexingText(scanResult, fullText)` returning `(CanIndex, Text, Reason)`.
2. Do not compute `textToIngest` as `MaskedContent ?? FullText` when PII is detected.
3. Audit/log the "PII detected but masked content unavailable" branch.
4. Clarify in PR documentation whether PII redaction means "masked indexing" rather than "block indexing".

### Required Tests

- `PiiDetected=true`, `MaskedContent="masked"` publishes embedding request with masked text only;
- `PiiDetected=true`, `MaskedContent=null/empty/whitespace` does not publish embedding request;
- `DlpDetected=true` does not publish embedding request;
- clean scan publishes embedding request with full text;
- audit metadata records detection flags.

### Acceptance Gate

No test path can publish `content.FullText` when `PiiDetected == true`.

## 6. Resolver B4: Avoid Constructor-time Failure For Missing Preview Signing Key

### Problem

`WorkspaceMemberService` resolves the role-preview signing key in its constructor. If `Security:RolePreviewSigningKey`, `WARPTALK_ROLE_PREVIEW_SIGNING_KEY` and a usable `Jwt:Secret` are absent, dependency injection fails and all member endpoints can return 500, including unrelated list/remove/update flows.

### Resolver

Resolve the signing key lazily in preview/apply methods only. Unrelated member operations must not depend on preview-token configuration.

Preferred order:

1. dedicated `Security:RolePreviewSigningKey`;
2. `WARPTALK_ROLE_PREVIEW_SIGNING_KEY`;
3. only as a temporary compatibility fallback, usable `Jwt:Secret`.

Also add config examples for local/dev deployments.

### Implementation Steps

1. Replace `_previewSigningKey` field with an injected configuration reference or a small `IRolePreviewSigningKeyProvider`.
2. Call the provider only from `PreviewMemberRoleChangeAsync` and `ApplyMemberRoleChangeAsync`.
3. Return a controlled `ValidationError` or `ServiceUnavailable` style result when preview signing is not configured.
4. Add `.env.example`, appsettings comments or deployment docs for `WARPTALK_ROLE_PREVIEW_SIGNING_KEY`.
5. Remove optional constructor parameter leakage if tests can inject a concrete configuration/provider.

### Required Tests

- `ListMembersAsync` succeeds when preview signing key is missing;
- `RemoveMemberAsync` succeeds/fails only according to membership rules, not signing config;
- preview/apply returns a controlled error when signing key is missing;
- preview/apply succeeds when dedicated key is configured;
- placeholder keys such as `CHANGE_ME` are rejected.

### Acceptance Gate

Missing preview signing configuration can break only preview/apply role-change operations, not the whole `WorkspaceMemberService`.

## 7. Resolver B5: Preserve Verified Domains When Omitted

### Problem

`UpdateWorkspaceSettingsAsync` computes removed domains using `settings.VerifiedDomains ?? new List<string>()`. In an auto-save or partial settings payload, omitted `VerifiedDomains` can be interpreted as an explicit empty list, causing failed updates or accidental domain removal.

### Resolver

Separate full settings replacement from partial update semantics:

- for existing `PUT`, either require full payload and validate `VerifiedDomains` is present;
- for auto-save/partial updates, use a dedicated `PATCH` request DTO where every field is nullable and omitted fields are unchanged;
- never treat omitted `VerifiedDomains` as "remove all".

Given PR #70 adds auto-save behavior, recommended resolver is to replace the raw `JsonObject` merge with a typed `WorkspaceSettingsPatchRequest`. The patch DTO should preserve field presence at the API boundary, merge into the current `WorkspaceSettingsDto`, then call the existing service with a complete settings object.

### Implementation Steps

1. Add `WorkspaceSettingsPatchRequest` with nullable properties for every auto-save field, including `List<string>? VerifiedDomains`.
2. Add a mapper/helper such as `ApplyPatch(currentSettings, patch)` that returns a full `WorkspaceSettingsDto`.
3. Update `PATCH /api/v1/workspaces/{id}/settings` to bind `WorkspaceSettingsPatchRequest`, not raw `JsonObject`.
4. Keep `PUT /settings` as full replacement and validate complete payload semantics separately.
5. In service-level removal detection, do not infer field presence from the full DTO alone. If removal logic needs presence, pass a command object that includes `VerifiedDomainsWasProvided`, or keep domain-removal checks in the PATCH merge layer.
6. Remove dead `!newDomainsSet.Contains(targetDomain)` check when `removedDomains` is already a set difference.
7. Add tests for omitted versus explicit empty domain list through the controller/API, not only service unit tests.

### Required Tests

- HTTP PATCH omitting `verifiedDomains` keeps current domains unchanged;
- HTTP PATCH with `"verifiedDomains": []` attempts removal and is blocked if active internal members depend on a domain;
- HTTP PATCH with `"verifiedDomains": []` succeeds only when removal guards pass;
- HTTP PATCH updating an unrelated field does not call revoke-domain guard logic;
- HTTP PUT remains a full replacement path and is covered by a separate full-payload test;
- model binding does not turn omitted `verifiedDomains` into an empty list before merge.

### Acceptance Gate

An auto-save payload for a non-domain field cannot remove or fail because of existing verified domains.

## 8. Resolver B6: Align Preview And Idempotency Claims With Reality

### Problem

The new preview/apply flow includes preview token, expiry, cooling-off and idempotency fields, but:

- legacy `PUT /members/{userId}/role` can bypass preview/apply;
- idempotency key is non-empty validation only;
- preview token is replayable for its validity window.

### Resolver

Be explicit about v1 guarantees.

Recommended v1:

- Owner-only authorization and target-role guardrails apply on both legacy and new routes;
- preview token provides freshness for new apply route only;
- idempotency key is correlation metadata, not durable replay dedupe;
- durable idempotency and single-use tokens are deferred until a persistence store is approved.

### Implementation Steps

1. Keep legacy route as compatibility adapter with Owner-only, no self-change, no Owner target, no External target.
2. Do not describe legacy route as protected by preview/cooling-off.
3. Update DTO/API docs to label idempotency key as correlation metadata unless a real store is added.
4. Add a follow-up issue/spec if durable idempotency is required before merge.
5. Consider returning `409 RoleChangeStale` on replay after the first successful role mutation.

### Required Tests

- Admin cannot use legacy route to change roles;
- Owner cannot use either route for self-change, Owner target or External target;
- new route rejects stale preview after target role changes;
- replay after successful apply does not emit duplicate events when state is already changed;
- docs/tests do not claim durable dedupe unless implemented.

### Acceptance Gate

There is no route that bypasses the mandatory role-change authorization guardrails.

## 9. Resolver B7: Decide Verified-domain Privilege Boundary

### Problem

PR #70 widens verified-domain add/revoke from Owner-only to Owner-or-Admin. Verified domains affect whether users are Internal, so this is an authorization boundary change.

### Resolver

Default to Owner-only unless the product owner explicitly approves Admin management.

If Admin access is approved, update all naming and documentation:

- rename `OnlyOwnerCanManageDomains`;
- update error messages;
- restore XML/API docs explaining the business rule;
- include the privilege expansion in PR title/body.

### Implementation Steps

1. Check `141-workspace-members/spec.md` and related workspace policy docs for Owner/Admin wording.
2. If no explicit approval exists, revert Add/Revoke guards to `IsOwner()`.
3. Keep List as Owner/Admin if settings visibility is intended for Admin.
4. Restore the removed no-DNS-challenge/business-rule comment in the service/API docs.
5. Add tests for Owner, Admin, Member and External behavior.

### Required Tests

- Owner can add/revoke verified domains;
- Admin cannot add/revoke unless product decision says otherwise;
- Admin can list only if settings access remains Owner/Admin;
- Member/External cannot list/add/revoke;
- revoke with active internal members is blocked.

### Acceptance Gate

Verified-domain mutation permissions match documented product policy and test names/error constants no longer contradict behavior.

## 10. Resolver B8: Update PR Process Metadata

### Problem

The PR title/body presents a large feature/security change as a chore with an empty description. Reviewers cannot safely evaluate authorization, PII and API changes from metadata.

### Resolver

Retitle and document the PR before requesting re-review.

Recommended title:

```text
feat(workspace): add auto-save settings and role governance safeguards
```

Minimum PR body:

- summarize role preview/apply behavior and compatibility route behavior;
- call out Owner-only role changes;
- call out verified-domain permission decision;
- call out PII masked-indexing policy;
- list config requirement for role preview signing;
- list tests added for each blocker.

### Acceptance Gate

Reviewer can understand all security, authorization and data-policy changes from the PR body without reading the full diff first.

## 11. Recommended Execution Order

1. Fix role-change outbox persistence.
2. Add correct role-change payload and delivery support.
3. Patch PII indexing guard to prevent raw-text fallback.
4. Make preview signing key lazy and document config.
5. Fix omitted `VerifiedDomains` semantics.
6. Align legacy route, preview and idempotency guarantees.
7. Resolve verified-domain Owner/Admin decision.
8. Add or update tests for every resolver.
9. Retitle and fill PR body.
10. Request re-review from `huynhthaitu124`.

## 12. Recycle Bug Control Matrix

These are the ways the same reviewed bugs can come back under a new shape. Each resolver must include its guard test.

| Recycle bug | Resolver guard | Required proof |
|---|---|---|
| Role-change event still not persisted because another role path enqueues after save. | Centralize role-change mutation through one core method and enqueue before the single commit. | Tests cover normal role change and transfer ownership. |
| Event is persisted but still carries removed-member schema. | Add explicit `MemberRoleChangedEventPayload` and assert serialized JSON fields. | Serialization test fails if payload type regresses. |
| PII path still falls back to raw text through a different helper/adapter. | Make indexing text resolution a single helper and test all scan-result combinations. | Mock embedding publisher captures text and proves raw text is absent when PII is detected. |
| Missing signing key still breaks unrelated member endpoints. | Remove constructor-time resolution and test service methods under missing config. | `ListMembersAsync`/`RemoveMemberAsync` run without preview key. |
| `VerifiedDomains` omit bug survives because model binding creates an empty list. | Use typed nullable PATCH DTO and controller-level tests. | HTTP PATCH omit case proves domains stay unchanged. |
| Legacy route remains a preview/idempotency bypass. | Route legacy calls through the same guardrail core and document lower guarantees. | Admin/self/Owner/External target tests cover both routes. |
| Admin verified-domain privilege sneaks back through one endpoint. | Test add, revoke and list separately by role. | Admin mutation tests fail unless product explicitly approves the expansion. |

## 13. New Bug Prevention Matrix

These are bugs the fixes themselves could introduce. They must be covered before re-review.

| Potential new bug | Prevention | Required tests |
|---|---|---|
| Duplicate role-change outbox rows on stale apply/replay. | Return `409 RoleChangeStale` or no-op before enqueue when current role already differs from preview old role. | Replay after successful apply does not enqueue a second event. |
| RabbitMQ/outbox delivery cannot deserialize the new payload. | Update delivery switch/deserializer in the same commit as the payload. | Outbox delivery test routes `MemberRoleChangedEventPayload`. |
| Consumer breaks because event version/name changes. | Keep event name stable and add payload fields without renaming the event. | Contract test asserts `workspace.member.role_changed`. |
| PII-with-empty-mask is marked with the wrong ingestion status for UI. | Choose and document one status: recommended `skipped` for policy skip, `failed` only for processing failure. | Test asserts the selected status and lifecycle event. |
| Lazy signing hides deployment misconfiguration. | Log a specific error and return a specific error code for preview/apply. | Missing-key preview/apply tests assert error code/message. |
| PATCH accidentally overwrites unrelated nested AI policy fields. | Merge nested objects field-by-field, not by replacing the whole `AiUsagePolicy` unless supplied. | PATCH one nested DLP field preserves redaction/profile/glossary values. |
| PUT and PATCH semantics diverge silently. | Document PUT as full replace and PATCH as partial merge. | Separate tests for PUT full replacement and PATCH partial merge. |
| Owner-only verified-domain mutation makes Admin UI show broken actions. | Backend tests plus frontend/API contract note requiring Admin UI to hide mutation controls. | Controller tests prove 403; frontend follow-up hides actions if in scope. |

## 14. Verification Checklist

- `dotnet test` for Workspace tests passes.
- Role-change persistence is covered by a DB-backed outbox test.
- Role-change payload JSON contains the correct schema.
- PII tests prove raw text is never indexed after PII detection.
- Member list/remove/update endpoints do not require preview signing config.
- Auto-save settings payloads use a typed PATCH DTO and do not mutate omitted domain fields.
- Legacy role-change route cannot bypass Owner-only authorization.
- Verified-domain mutation policy is documented and tested.
- PR description lists all behavior/security changes.

## 15. Deferred Follow-ups

These should not be hidden inside the blocker fix unless explicitly approved:

- durable role-change idempotency store;
- single-use preview token with nonce/jti persistence;
- role-change audit/history endpoint;
- shared helper for domain-in-use checks across settings update and verified-domain revoke;
- bounded concurrency or database-backed search for `ListMembersAsync`;
- transaction API policy for `IUnitOfWork` after the outbox pattern is settled.
