# Workspace Dashboard, Navigation & Members Management Specification

## Goal

Redesign and consolidate the workspace onboarding and governance UI to match the premium Linear UI style. This specification details the migration of workspace and meeting pages into a dynamic Next.js slug structure (`[workspaceSlug]`), eliminating the nested double-sidebar layout, and updating `middleware.ts` for clean B2B landing page redirects.

---

## Resolved Decisions

- **Combined Sidebar**: Eliminate the inner `WorkspaceSidebar` duplicate sidebar. Delete the file `src/components/layout/workspace-sidebar.tsx`. Integrate all workspace-specific navigation directly into the global `LinearSidebar`.
- **Dynamic B2B Slug Routing**:
  - Migrate application routes into a dynamic folder structure under `src/app/(app)/[workspaceSlug]/`.
  - The mapped paths will be:
    - Meetings list: `/[workspaceSlug]/rooms`
    - History: `/[workspaceSlug]/history`
    - AI Summaries: `/[workspaceSlug]/ai-summaries`
    - Workspace Dashboard: `/[workspaceSlug]/dashboard` (restricted to Owner/Admin)
    - Members: `/[workspaceSlug]/members`
    - Workspace Terminology: `/[workspaceSlug]/documents` (Workspace documents library)
    - Settings: `/[workspaceSlug]/settings` (Workspace Settings)
  - Keep `/host/dashboard` unmodified for now (revisiting/renaming it in a later phase).
- **Post-Login/Landing Redirects**:
  - In `middleware.ts`, if a logged-in user hits `/` or `/dashboard`, the middleware reads the `active_workspace_slug` cookie (e.g. `fpt-sep490-su26`) and redirects them to `/[workspaceSlug]/rooms` as their default landing page.
- **Dynamic Workspace Header**: Display the active workspace name dynamically in the sidebar header. Render a clean initials badge (e.g. "FP" or "WS") derived dynamically from the workspace name.
- **Documents & Settings Routing**: 
  - Point the `Terminology` sidebar link under the Workspace section to `/[workspaceSlug]/documents` using the existing documents API.
  - Point the `Settings` sidebar link under the Workspace section to `/[workspaceSlug]/settings` using the workspace settings API.
  - Keep the original personal `/terminology` page and transcript schema glossary tables completely untouched.
- **Members List simplification**: Redesign `/[workspaceSlug]/members` to match the Linear dark table format. Display user roles, active status (Online for the current user, Offline/recent date for others), and remove the "Application" section entirely.
- **Domain Verification Preview**: Restore the domain validation preview text in `workspace/create/page.tsx` (`Workspace will be verified for {emailDomain}`).
- **Demo Seed Data**:
  - Seed workspace `FPT-SEP490-SU26` (slug: `fpt-sep490-su26`) owned by `demo@enterprise.vn`.
  - Seed 4 additional accounts: `alice.smith@enterprise.vn` (Admin), `bob.johnson@enterprise.vn` (Member), `charlie.brown@fpt.edu.vn` (Member), `diana.prince@fpt.edu.vn` (Member).
  - Seed sample reference documents in `workspace.workspace_documents` to populate the library.

---

## Target Web Files

| File | Change Details |
|---|---|
| `warptalk-web/src/components/layout/linear-sidebar.tsx` | Add dynamic slug-based links (Dashboard, Members, Workspace Terminology, Settings) to the Workspace section. Point `Terminology` to `/[workspaceSlug]/documents` and `Settings` to `/[workspaceSlug]/settings`. |
| `warptalk-web/src/app/(app)/workspace/layout.tsx` | Simplify to return only `{children}` with padding for non-onboarding routes, removing the nested `WorkspaceSidebar`, `Topbar`, and inner frames. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/dashboard/page.tsx` | **[NEW]** Workspace dashboard page nested under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/members/page.tsx` | **[NEW]** Redesigned members list under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/documents/page.tsx` | **[NEW]** Workspace documents page under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/settings/page.tsx` | **[NEW]** Workspace settings page under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/rooms/page.tsx` | **[NEW]** Workspace rooms page under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/history/page.tsx` | **[NEW]** Workspace history page under slug. |
| `warptalk-web/src/app/(app)/[workspaceSlug]/ai-summaries/page.tsx` | **[NEW]** Workspace AI summaries page under slug. |
| `warptalk-web/src/middleware.ts` | Redirect `/` and `/dashboard` to `/[workspaceSlug]/rooms` using `active_workspace_slug` cookie. |
| `warptalk-web/src/app/(app)/layout.tsx` | Update breadcrumbs parser to extract and handle `[workspaceSlug]` cleanly. |

---

## Implementation Steps

### Step 1 - Dynamic Slug Folder Structure
- Create folder `src/app/(app)/[workspaceSlug]` and move the relevant subfolders (`rooms`, `history`, `ai-summaries`, `workspace/dashboard`, `workspace/members`, `workspace/documents`, `workspace/settings`) inside it.
- Delete `workspace-sidebar.tsx` file.

### Step 2 - Middleware & Layout Updates
- Update `middleware.ts` to implement cookie-based workspace slug redirection for logged-in users landing on `/` or `/dashboard`.
- Update `linear-sidebar.tsx` to dynamically query and construct pathnames using the current active workspace slug from Zustand store.
- Update `workspace/layout.tsx` to strip out nested sidebars and headers.
- Update breadcrumb builder in `app/layout.tsx` to parse dynamic workspace slugs.

### Step 3 - Page Redesigns & Previews
- Restore the email domain validation preview below the URL slug field on the Workspace Create page.
- Redesign the members page inside `[workspaceSlug]/members/page.tsx` using a clean borderless table layout with status indicators.
