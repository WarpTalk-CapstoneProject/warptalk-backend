# Workspace Module Context

Workspace is the Enterprise tenant boundary for WarpTalk collaboration, governance, documents, meetings, artifacts, and billing context.

## Language

**Enterprise Workspace**:
The only workspace model currently supported by WarpTalk. It represents an organization-scoped tenant boundary; there is no personal/default workspace type.
_Avoid_: Personal workspace, workspace type

**Internal Home Workspace**:
The single domain-verified Enterprise Workspace where a user is treated as an Internal member. A user may belong to many Enterprise Workspaces, but may be Internal in at most one domain-verified Enterprise Workspace.
_Avoid_: Primary workspace, default workspace, home tenant

**External Workspace Membership**:
Membership in an Enterprise Workspace where the user participates as an External Member. This can coexist with the user's Internal Home Workspace and with other external memberships.
_Avoid_: Guest account, secondary internal membership

**Verified Domain**:
A company domain registered and verified for one active Enterprise Workspace. The backend enforces uniqueness for active verified domains through `workspace.workspace_verified_domains`, so the same active verified domain cannot belong to multiple enterprises.
_Avoid_: Email suffix, tenant domain

**Workspace Onboarding Gate**:
The full-screen entry surface shown when an authenticated user has no active workspace context. It lets the user choose whether to join an existing workspace or create a new Enterprise Workspace before workspace-scoped resources are shown.
_Avoid_: Sidebar workspace page or workspace list/table in the no-active-context demo path
