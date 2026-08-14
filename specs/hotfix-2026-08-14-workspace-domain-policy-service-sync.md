# Hotfix: workspace verified-domain policy lives in service logic
Date: 2026-08-14
Reporter: Codex review sync for backend PR 189 and infrastructure PR 105

## Bug
Workspace domain policy could still depend on one-time database repair instead of the ongoing backend workflow. A workspace could add, revoke, or soft-delete verified domains without the `require_verified_domain_for_internal` column being kept in sync by the service layer.

## Root Cause
`RequireVerifiedDomainForInternal` was still accepted as settings payload state, while the actual policy invariant is derived from active `workspace_verified_domains` rows. `AddDomainAsync`, `RevokeDomainAsync`, and `SoftDeleteWorkspaceAsync` did not consistently update the policy column when domain state changed.

## Fix
Derive verified-domain policy in the workspace service layer from active verified-domain rows. Reject settings payloads that try to move the derived flag directly, enable the policy when adding a verified domain, disable it when the last active verified domain is revoked, and revoke active domains during workspace soft delete.

## Verification
- `dotnet test workspace\tests\WarpTalk.WorkspaceService.Tests\WarpTalk.WorkspaceService.Tests.csproj --filter "FullyQualifiedName~VerifiedDomainServiceTests|FullyQualifiedName~WorkspaceServiceTests"`
- `dotnet test workspace\tests\WarpTalk.WorkspaceService.Tests\WarpTalk.WorkspaceService.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- `dotnet build warptalk-backend.slnx`

## Regression Risk
Settings clients can no longer use `RequireVerifiedDomainForInternal` as a writable setting. They must add or revoke verified domains to change that derived policy. Existing callers that send a stale value will receive a validation error and should refresh settings before retrying.
