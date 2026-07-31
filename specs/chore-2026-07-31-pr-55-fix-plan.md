# Chore: PR #55 Fix Plan

Date: 2026-07-31
Branch: chore/update-auto-save-settings-pages
Pull Request: https://github.com/WarpTalk-CapstoneProject/warptalk-backend/pull/55

## Scope

Fix the remaining blocking and actionable issues on PR #55 while enforcing the
intended workspace settings permission model.

Most workspace settings are editable by workspace Owner and Admin.
`AllowExternalCollaboration` is a sensitive workspace policy and must be
editable by the workspace Owner only. Restore the Owner-only guard for changes
to this field.

Workspace member role changes are intentionally Owner-only. Do not treat Admin
role-change denial as a bug.

Naming should match behavior: Owner-only policy settings should use one shared
guard concept and one shared error constant. General settings update errors may
keep Owner/Admin wording.

`AllowExternalCollaboration` should not have a separate permission branch when
its behavior is identical to other Owner-only policy settings. Fold it into the
shared Owner-only policy-settings check and expose the behavior through an
Owner-only policy name such as `OnlyOwnerCanModifyPolicySettings`.

## Infrastructure Config Note

Meeting Service runtime configuration is already provided by
`warptalk-infrastructure/docker-compose.yml` through environment variables:

- `ConnectionStrings__DefaultConnection`
- `Jwt__Secret`, `Jwt__Issuer`, `Jwt__Audience`
- `Grpc__InternalSecret`
- `LiveKit__Url`, `LiveKit__ApiKey`, `LiveKit__ApiSecret`
- `LiveKit__Egress__S3__*`
- `Redis__ConnectionString`
- `Smtp__*`
- `OpenAI__*`
- `Storage__*`

Therefore `meeting/src/WarpTalk.MeetingService.API/appsettings.json` should not
contain credential-like fallback values. Keep only safe local defaults and move
secrets to env-provided configuration.

## Fix Plan

### 1. Unblock Secret Scanning

- Remove credential-like values from Meeting `appsettings.json`.
- Replace secret placeholders with empty strings or omit those keys when the
  application already fails fast on missing configuration.
- Review `run-all-local.ps1` for hardcoded local credentials that trigger
  secret scanners.
- Rotate any real leaked credential.
- Rewrite or squash PR branch history after removal because GitGuardian scans
  introduced commits, not only the final diff.

### 2. Repair `run-all-local.ps1`

- Fix the service startup loop structure. The current patch closes
  `foreach ($service in $Services)` before the startup body.
- Move folder checks, `Start-Process`, env setup, env cleanup, and startup delay
  back inside the loop.
- Remove duplicated nested `if ($name -eq "auth")` blocks.
- Decide whether migrations/seeds are intentionally disabled. If they are,
  document the switch; otherwise restore `Invoke-Migrations` and `Invoke-Seeds`.
- Validate the script parses before running it.

### 3. Stabilize Role Preview Signing

- Remove the process-random `FallbackPreviewSigningKey`.
- Require a stable signing key from `Security:RolePreviewSigningKey` or another
  agreed env-backed configuration key.
- Fail fast when the key is missing or invalid instead of silently generating a
  per-process key.
- Add tests that prove a preview token remains valid across two service
  instances configured with the same key.
- Add tests that prove missing signing key configuration fails deterministically.

### 4. Restore Member Search Behavior

- Preserve DB-level pagination when `GetWorkspacesQuery.Search` is empty.
- When `Search` is present, ensure filtering by user full name and email still
  works.
- Short-term implementation option: fetch candidate workspace members, resolve
  user profiles from Auth, filter in memory, then paginate the filtered result.
- Long-term implementation option: add a workspace-member profile read model so
  search can stay DB-level.
- Add tests for filtered member results and filtered `totalCount`.

### 5. Optimize Verified Domain Revocation Check

- Replace nested sequential `_authIdentity.GetUserByIdAsync` calls with a single
  `Task.WhenAll` over active internal members.
- Compare removed domains against fetched email domains in memory.
- Add coverage for rejecting domain removal when active internal members still
  use that domain.

### 6. Restore Owner-Only External Collaboration Policy

- In `WorkspaceService.UpdateWorkspaceSettingsAsync`, compare
  `currentConfig.AllowExternalCollaboration` with
  `settings.AllowExternalCollaboration`.
- Treat the comparison as part of a shared Owner-only policy-settings guard,
  e.g. `ownerOnlyPolicyChanged`.
- Do not keep duplicate external-collaboration-only permission logic if it
  returns the same result as the shared Owner-only policy guard.
- If any Owner-only policy value changes and the executing role is not Owner,
  return a Forbidden result.
- Use one shared error constant for this class of settings:
  `OnlyOwnerCanModifyPolicySettings`.
- Stop using the narrower `OnlyOwnerCanModifyExternalCollaboration` constant in
  this flow; remove it if no call sites remain.
- Keep other workspace settings editable by Owner/Admin.
- Add or adjust tests:
  - Owner can change `AllowExternalCollaboration`.
  - Admin can update non-sensitive settings.
  - Admin cannot change `AllowExternalCollaboration`.
  - Admin receives `OnlyOwnerCanModifyPolicySettings` when changing an
    Owner-only policy setting.

### 7. Fix Role-Change Naming Mismatch

- Role changes are Owner-only in PR #55.
- Rename `OnlyOwnerAdminCanChangeRoles` to an Owner-only name such as
  `OnlyOwnerCanChangeRoles`.
- Update call sites in role preview/apply/change flows.
- Keep legacy Admin-specific role-change constants only if still referenced by
  code or tests after the Owner-only rule is applied; otherwise remove them with
  the same behavioral test coverage intact.

### 8. Clean Workspace Service Private Helpers

- Keep PR #55 service classes aligned with Clean Architecture/SOLID by moving
  reusable private helper logic out of service classes.
- Extract workspace member DTO composition and role-preview signing-key
  resolution to Application helper classes.
- Extract invitation acceptance processing to an Application helper while
  keeping behavior and error handling unchanged.
- Extract document guardrail eligibility/skip state transition helpers out of
  the background service into an Infrastructure helper.
- Avoid broad unrelated refactors outside the PR #55 workspace service surface.

## Verification Checklist

- `dotnet build warptalk-backend.slnx`
- Workspace service tests
- Meeting service tests
- Search confirms no private helper methods remain in PR #55 workspace service
  classes.
- PowerShell parse check for `run-all-local.ps1`
- GitGuardian check is green after history cleanup
- PR review response notes that most workspace settings are Owner/Admin, but
  `AllowExternalCollaboration` and member role changes are Owner-only
