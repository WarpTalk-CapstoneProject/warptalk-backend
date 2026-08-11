# Business Rules Table Review

Source set: `warptalk-backend/specs/` + `warptalk-backend/` codebase.
Feature taxonomy source: `My Drive/Report1/Report1_Project Introduction.docx`.
This file is the source table for the Report3 business-rule appendix.
Overlapping rules from multiple specs were merged into one appendix rule where they describe the same enforced business behavior.

## Legend

- `Feature`: Report1 feature classification for the business rule. Multiple values mean the rule crosses feature boundaries.
- `[Future/Proposed]`: kept inside the rule definition when the source spec marks the behavior as future/proposed or when implementation evidence is insufficient for shipped behavior.

## Feature Taxonomy From Report1

- `FE-01`: Auth & Workspace Management
- `FE-02`: Live Meeting & Collaboration
- `FE-03`: Real-time Translation Session
- `FE-04`: Transcript & AI Meeting Intelligence
- `FE-05`: External Platform Integration
- `FE-06`: Billing & Subscription Management
- `FE-07`: Administration & System Management

## Business Rule Table

| ID | Rule Definition | Feature |
|---|---|---|
| BR-01 | Disabled, locked, blocked, or soft-deleted accounts must not obtain or refresh a normal app session. | FE-01 |
| BR-02 | A new local account must verify its email before first normal login. | FE-01 |
| BR-03 | Consecutive failed password logins trigger temporary lockout and login stays blocked until the lock window expires or an admin unlocks the account. | FE-01 |
| BR-04 | Google login/linking is allowed only for a verified Google identity, and automatic linking to an existing local account is allowed only when that local account is already email-verified. | FE-01 |
| BR-05 | Google account linking must fail when the Google email does not exactly match the existing account email, and unlinking Google is forbidden if it would leave the user without any usable authentication method. | FE-01 |
| BR-06 | Refresh-token families must be revoked on logout and after password change so a stolen refresh token cannot keep rotating into fresh sessions. | FE-01 |
| BR-07 | Users may read or update only their own application preferences, and language/timezone preference values must use the supported standard formats. | FE-01 |
| BR-08 | WarpTalk currently supports a single Enterprise Workspace model, and workspace behavior is governed inside that model by workspace policy and subscription entitlements rather than by a separate personal-workspace branch. | FE-01 |
| BR-09 | Creating a workspace must bootstrap the creator as the first active Owner member of that workspace. | FE-01 |
| BR-10 | Each workspace must have exactly one current active workspace-scoped Owner. The Owner role exists only within that workspace and only through active membership in that workspace. | FE-01 |
| BR-11 | Only the current workspace-scoped Owner can transfer ownership, and the new owner must be an active non-external member so the handover preserves exactly one current active Owner. | FE-01 |
| BR-12 | Only Workspace Owner/Admin may update workspace settings. | FE-01 |
| BR-13 | Owner-only workspace governance policies remain reserved to the current Owner. | FE-01 |
| BR-14 | Workspace role elevation and demotion are Owner-only actions. | FE-01 |
| BR-15 | Workspace Admins may perform only explicitly allowed operational member actions and must not manage the Owner, manage peer Admins, or self-grant restricted meeting permissions. | FE-01; FE-02 |
| BR-16 | Removing a workspace member must remove that member from normal workspace authorization without deleting historical audit, billing, meeting, or document records. | FE-01; FE-02; FE-06; FE-07 |
| BR-17 | Only Owner/Admin can create invitations. | FE-01 |
| BR-18 | External Members are restricted collaborators inside a workspace. | FE-01 |
| BR-19 | External Members must remain `MembershipType=External` with role `Member`. | FE-01 |
| BR-20 | External Members must not receive Owner or Admin authority. | FE-01 |
| BR-21 | External Members must not create invitations or manage workspace settings, billing, or ownership. | FE-01 |
| BR-22 | External Members must not access unrestricted internal member data. | FE-01 |
| BR-23 | External Members may access only resources explicitly allowed within the approved collaboration scope. | FE-01 |
| BR-24 | An active Workspace Owner may add a verified company domain only when the domain is non-public and matches the Owner's account email domain. | FE-01 |
| BR-25 | Owner-added company domains are treated as verified for workspace policy purposes because legal/domain verification is outside system scope and trusted to the Owner. | FE-01 |
| BR-26 | When verified-domain enforcement is enabled, active verified company domains are the source of truth for Internal membership classification. | FE-01 |
| BR-27 | Public email domains cannot be used as verified enterprise domains. | FE-01 |
| BR-28 | One active verified company domain can belong to at most one workspace. | FE-01 |
| BR-29 | When verified-domain enforcement is disabled, Internal invitations do not require domain validation. | FE-01 |
| BR-30 | A single user account may belong to multiple workspaces. | FE-01 |
| BR-31 | A user may hold active Internal membership in at most one Enterprise Workspace at a time. | FE-01 |
| BR-32 | Additional workspace memberships for that user must remain External and are still subject to each workspace's collaboration policy. | FE-01 |
| BR-33 | Invitations expire after the workspace's configured invitation window, which defaults to 7 days. | FE-01 |
| BR-34 | Resending a still-pending invitation must mark the previous token as `REPLACED`. | FE-01 |
| BR-35 | Only the newest pending and unexpired invitation token is acceptable. | FE-01 |
| BR-36 | Invitation acceptance is identity-bound to the invited email. | FE-01 |
| BR-37 | Invitation acceptance must preserve the inviter's intended membership classification. | FE-01 |
| BR-38 | The system may admit an invited user only as the invited Internal/External membership type or reject the invitation. | FE-01 |
| BR-39 | Domain policy is evaluated using the workspace's current verified-domain enforcement setting, but the system must not silently reclassify an invitation into a different access class. | FE-01 |
| BR-40 | External collaboration must be explicitly enabled before outside-domain users can be invited or approved into the workspace. | FE-01 |
| BR-41 | Join requests must create Member-level access only, and only Workspace Owner/Admin may approve or reject them. | FE-01 |
| BR-42 | Duplicate active join requests for the same workspace/email must be blocked. | FE-01 |
| BR-43 | Workspace-scoped core features require an active workspace context, and preflight checks must treat inactive or soft-deleted workspaces as unavailable so the requested action stops early. | FE-01; FE-02; FE-03; FE-04; FE-06; FE-07 |
| BR-44 | For join requests, approval must atomically convert the request to `ACCEPTED` and create exactly one workspace membership; rejection changes only review tracking and status to `REJECTED`. | FE-01 |
| BR-45 | A participant may join only a room that exists and is not already in a terminal lifecycle state. | FE-02 |
| BR-46 | The host bypasses approval and joins as `CONNECTED/HOST`; non-host join state is driven by the room's `requires_approval` setting. | FE-02 |
| BR-47 | A kicked participant is permanently blocked from rejoining the room unless a future explicit re-invite flow changes that policy. | FE-02 |
| BR-48 | Room settings may only be changed while the room is not yet in active live execution; later joiners must obey the latest saved settings. | FE-02 |
| BR-49 | Room lifecycle transitions must stay legal and consistent across `SCHEDULED`, `WAITING`, `IN_PROGRESS`, `PAUSED`, `ENDED`, `CANCELLED`, and `EXPIRED`, and discarded drafts must not be preserved as room lifecycle records. | FE-02 |
| BR-50 | Only authorized host-side controls may reject waiting users, kick participants, transfer host authority, or end a live room. | FE-02 |
| BR-51 | Participant lifecycle state (`CONNECTED`, `DISCONNECTED`, `LEFT`, `KICKED`, `REJECTED`, etc.) is separate from per-participant translation-audio mute state. | FE-02 |
| BR-52 | Workspace language policy must bound room language choices; unsupported source/target language combinations must be rejected before live processing. | FE-03; FE-02 |
| BR-53 | Meeting reminders must fire at most once per reminder window and avoid duplicate sends during worker retries. | FE-02; FE-07 |
| BR-54 | Only users who are legitimately in room scope may view/send room chat, and moderation must be able to hide normal visibility without erasing auditability of the original content. | FE-02; FE-04 |
| BR-55 | Transcript access is limited to the host and actual room participants; an invited email that never became a participant is not enough to read the transcript. | FE-04 |
| BR-56 | Transcript correction is allowed only when the parent transcript is finalized and the target segment exists; corrections must preserve source linkage, and re-translation must not overwrite the audit trail of the original source text. | FE-04; FE-03 |
| BR-57 | Global glossary terms are platform-level assets managed by System Admin separately from workspace glossary terms; only published global glossary items may be used for new processing, and archived items must not be used. | FE-04; FE-03; FE-07 |
| BR-58 | Workspace documents are enterprise-owned assets governed by workspace membership and policy. | FE-01; FE-04 |
| BR-59 | Document access policy uses deny-overrides: any matching `DENY` blocks access, and access is allowed only when no applicable deny remains and the actor still qualifies under an allowed or default access path. | FE-01; FE-04 |
| BR-60 | Sensitive or pending-ingestion documents are fail-closed: only privileged actors may access them until the document reaches a safe completed state. | FE-01; FE-04 |
| BR-61 | External members may access documents only when explicitly granted or when a meeting-scoped exception says they may, typically within the configured grace period after a meeting they actually participated in. | FE-01; FE-02; FE-04 |
| BR-62 | Unsupported file type or oversize document upload must be rejected. | FE-01; FE-04 |
| BR-63 | A document may be used as AI retrieval context only when it is active, retention-active, ingestion-completed, and AI-eligible; soft-deleted, archived, expired, pending, or ineligible documents must be excluded. | FE-04 |
| BR-64 | Sensitive document upload/view/download/delete actions must produce an audit trail. | FE-01; FE-07 |
| BR-65 | A workspace may hold only one active subscription at a time. | FE-06 |
| BR-66 | Paid AI and other billable operations may run only when the workspace remains eligible under subscription state, AI service state, available credits, and overage policy. | FE-06 |
| BR-67 | If credits are insufficient, or overage is not allowed or has been exceeded, AI operations must be blocked immediately rather than continuing as unpaid usage. Credit balance must not become negative. | FE-06 |
| BR-68 | Billing is metered by usage event and processing type using the configured usage-rate-card unit for that service, such as seconds/minutes, token input/output, characters, or request count, rather than assuming a single word-count model. Charges apply only when the service is actually used. | FE-06 |
| BR-69 | Every successful AI credit charge or credit modification must create an immutable credit-ledger transaction with traceable reference fields. | FE-06 |
| BR-70 | Credit settlement must be idempotent and concurrency-safe so the same billable event is not charged more than once. | FE-06 |
| BR-71 | Subscription cancellation must support a controlled cancellation path appropriate to context, including immediate cancellation for trial-style flows and end-of-period cancellation for ongoing paid subscriptions. | FE-06 |
| BR-72 | Credit and payment histories must be paginated and sorted newest first. | FE-06 |
| BR-73 | Workspace billing and payment management are restricted to the Workspace Owner or authorized Workspace Admin. Regular members cannot manage billing operations such as plans, invoices, or top-ups. | FE-06 |
| BR-74 | Platform subscription plan management is restricted to System Admin, and deactivated plans must not remain customer-selectable for new purchases. | FE-06; FE-07 |
| BR-75 | Notification preferences default to enabled channels on first use, and delivery must respect the stored per-user preference matrix. | FE-07 |
| BR-76 | Users may read or mark only notifications in their own inbox, and notification inbox reads must stay paginated and bounded; page-size abuse should be clamped rather than returning unbounded data. | FE-07 |
| BR-77 | Admin broadcast notifications must validate payload schema by notification type, sanitize unsafe content, reject the entire targeted request when any target user is invalid or ineligible, and chunk fan-out so one broker event does not carry an unbounded recipient list. | FE-07 |
| BR-78 | Admin-triggered notification publishing must not block the HTTP response on downstream delivery; enqueue/publish is the synchronous success boundary. | FE-07 |
| BR-79 | System-admin workspace actions such as suspend/reactivate must be gated by the shared System Admin policy and recorded into an admin audit log. | FE-07 |
| BR-80 | Internal meetings are workspace-owned tenant assets; membership, policy, and retention decisions for those meetings must not bypass the workspace boundary. [Future/Proposed] | FE-02; FE-01 |
| BR-81 | Meeting creation is allowed only for members whom the workspace has granted meeting-creation authority, and each request must still satisfy workspace governance limits such as active-room quota and allowed-language policy. | FE-02; FE-01 |
| BR-82 | Users outside the workspace should be denied from joining internal meetings by default unless an explicit, policy-valid collaboration path says otherwise. [Future/Proposed] | FE-02; FE-01 |
| BR-83 | Transcript, summary, and artifact retention for workspace-owned meetings should follow workspace governance settings and preserve audit metadata after cleanup. [Future/Proposed] | FE-04; FE-07 |
| BR-84 | Removing a member from a workspace should evict them from active internal meetings fast enough that they cannot continue using the room as if membership still existed. [Future/Proposed] | FE-02; FE-01 |
| BR-85 | Workspace deletion or suspension should cascade into active internal meeting termination or hard blocking of new room activity under that tenant. | FE-02; FE-01; FE-07 |
| BR-86 | Meeting resource quotas such as participant count and language entitlements should follow workspace subscription terms rather than room-local assumptions. | FE-06; FE-02; FE-03 |
| BR-87 | Verification-email resend flows must enforce per-account cooldown and rate-limit windows. The same abuse-control policy also applies when another auth flow, such as Google login/linking, auto-triggers a new verification email for an unverified local account. | FE-01 |
| BR-88 | High-risk role elevation must require explicit, fresh user confirmation and must reject stale or superseded approvals. | FE-01 |
| BR-89 | High-risk workspace governance mutations such as ownership transfer and workspace deletion must require explicit user confirmation and must not be silently re-applied from stale or duplicate submissions. | FE-01; FE-07 |
| BR-90 | Password-reset and email-verification tokens must be single-use and must expire after their configured validity period. | FE-01 |
| BR-91 | Live translation/audio processing may proceed only with valid generated audio routes, and completed audio routes are terminal records that must not be updated. | FE-03; FE-02 |
| BR-92 | Scheduled meeting links are time-bound and must expire after the configured scheduled-start grace window; sessions with no connected participants must be ended automatically after the configured idle window. | FE-02 |
| BR-93 | Recurring meeting occurrences must be materialized from the recurrence rule without creating duplicate occurrences for the same series occurrence date. | FE-02 |
| BR-94 | Voice cloning must be opt-in by the speaker, default to disabled, apply only to the speaker's own outgoing voice routes, and remain subject to active plan entitlements, quota, and fallback policy. | FE-03; FE-06 |
| BR-95 | Pending document approval is restricted to Workspace Owner/Admin, while document access-policy mutation is restricted to Workspace Owner/Admin or the document owner when policy allows; these mutations must preserve auditability. | FE-01; FE-04; FE-07 |
| BR-96 | Admin broadcast notifications must store the intended audience mode such as broadcast, segment, or specific users; a broadcast is system-wide unless an explicit scope is provided. | FE-07 |
| BR-97 | Suspended, inactive, or soft-deleted workspaces must block new room admission, room start, AI processing, and other normal workspace-scoped operations until the workspace is eligible again. | FE-01; FE-02; FE-03; FE-06; FE-07 |

## Moved Out Of Business Rule Table

- Architecture / design note:
  `TranslationRoom` should consume workspace governance through service-boundary contracts such as gRPC, rather than direct cross-service database joins.
- Open future note:
  Meeting-creation rate limiting may still be desirable as an anti-abuse policy, but the current table should not present it as an established business rule until the product behavior is explicitly committed.

## Notes For Doc Embedding

- Report3 uses this 3-column appendix table: `ID`, `Rule Definition`, and `Feature`.
- Feature values stay aligned to the Report1 feature tree.
- Rows tagged `[Future/Proposed]` should not be written into the main SRS narrative as shipped behavior unless the document explicitly calls out that status.
- The old Report3 2-column business-rule table was used as a reference for coverage and appendix wording, but exact wording was not copied back when the reviewed source-of-truth table had merged, renumbered, or corrected the rule.
- Safety-policy cleanup in this pass:
  - `BR-68` merges the scattered resend-verification cooldown/rate-limit rules from WT-135 and the Google-triggered verification resend rule from WT-137.
  - External-member RBAC overlap was merged into `BR-15` so it is described once near the core workspace-governance rules.
  - Multi-workspace / single-internal-membership overlap was merged into `BR-18` so it is described once near the verified-domain and membership-classification rules.
  - Billing-specific source-check notes should live in `specs/business-rule/billing-business-rules.md` before promotion into this master appendix table.
