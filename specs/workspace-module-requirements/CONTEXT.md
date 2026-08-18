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
A company domain registered and verified for one active Enterprise Workspace. A workspace may hold several; a domain may be held by only one workspace at a time, enforced by the partial unique index on `workspace.workspace_verified_domains`.
_Avoid_: Email suffix, tenant domain

**Domain-Verified Membership**:
The membership policy of a workspace that holds at least one verified domain. Owner and Admin still choose each invitee's access type; what the policy adds is a constraint on that choice — `Internal` requires the invitee's address to be on a verified domain.
_Avoid_: Enterprise workspace (every workspace is one), strict mode

**Manually-Assigned Membership**:
The membership policy of a workspace that holds no verified domain. `Internal` and `External` mean exactly what they mean everywhere else and are chosen the same way; they are simply not constrained by the email domain. A workspace in this state is not a lesser kind of workspace and not a separate type.
_Avoid_: Non-Enterprise workspace, small workspace, personal workspace, free workspace

**Verification Method**:
What backs a verified-domain claim: `owner_email` (the domain matches the claiming account's own address, so the account is the evidence) or `self_asserted` (any other domain, recorded together with the Owner's consent, since nothing else can attest to it). `dns_txt` is reserved for WT-157 and issued by no path today.
_Avoid_: Trusted, system (both were used as literals and named nothing)

**Workspace Onboarding Gate**:
The full-screen entry surface shown when an authenticated user has no active workspace context. It lets the user choose whether to join an existing workspace or create a new Enterprise Workspace before workspace-scoped resources are shown.
_Avoid_: Sidebar workspace page or workspace list/table in the no-active-context demo path
