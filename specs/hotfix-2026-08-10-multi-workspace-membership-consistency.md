# Hotfix: Multi-workspace membership consistency

Date: 2026-08-10
Reporter: Codex audit from active thread goal

## Bug

Workspace selection and creation flows were inconsistent across backend and frontend:

- frontend deep-link and onboarding flows hydrated active workspace state from local list data instead of the backend select response;
- some frontend paths hardcoded `Internal` membership type when switching workspace;
- `GET /workspaces/{id}` did not reject deleted or inactive workspaces consistently with select flow;
- workspace list visibility could still include inactive/deleted workspaces or suspended memberships;
- create-workspace UI blocked users merely because they already had an active workspace, which was stricter than backend policy.

## Root Cause

- `SelectWorkspaceResponse` did not carry `role` or `membershipType`, so clients guessed or hardcoded them.
- Backend lifecycle guards were applied in `SelectWorkspaceAsync` but not consistently in `GetWorkspaceByIdAsync` and list visibility.
- Frontend routing layers updated Zustand directly from cached list items rather than the authoritative backend selection contract.

## Fix

- Extend `SelectWorkspaceResponse` to return `role` and `membershipType`.
- Hydrate every web select path from the backend response through one shared helper.
- Apply deleted/inactive guards in `GetWorkspaceByIdAsync`.
- Filter workspace list to active memberships in active workspaces only.
- Allow create-workspace UI for business-email users even when another workspace is currently active; keep backend as the final enforcement boundary.

## Verification

- `dotnet test workspace/tests/WarpTalk.WorkspaceService.Tests/WarpTalk.WorkspaceService.Tests.csproj --no-restore --filter "FullyQualifiedName!~Integration" --nologo`
- `dotnet test workspace/tests/WarpTalk.WorkspaceService.Tests/WarpTalk.WorkspaceService.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceSelectionIntegrationTests" --nologo`
- `npm run typecheck`
- `npm run lint`
- `npm run test:workspace-routes`
- `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --experimental-strip-types --test src/lib/workspace/__tests__/apply-selected-workspace.test.ts`

## Regression Risk

- The select contract changed, so every web path that consumes workspace selection must use the new response shape.
- Tightening list/get lifecycle filtering may hide stale workspaces that previously appeared due to inconsistent repository predicates.
