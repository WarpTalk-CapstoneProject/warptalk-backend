# Workspace Navigation & Personal Settings Specification

## Goal

Implement the workspace navigation shell and personal profile settings page for authenticated users who have selected a workspace context. The sidebar uses the Linear UI theme (`linear-sidebar.tsx`) and remains visible on all primary application routes (like `/host/dashboard`, `/rooms`, `/history`, and `/settings`).

This document maps out the specific implementation steps, target files, and API contracts for the Auth and Workspace features.

---

## Resolved Decisions

- **Host Dashboard Landing**: When the user successfully joins or creates a workspace, they must be redirected to `http://localhost:3000/host/dashboard` (instead of `/workspace/dashboard`), keeping the URL stable.
- **Dynamic Workspace Selector**: The sidebar header dropdown displays the currently active workspace name and uses dynamic initials. Clicking it shows a dropdown with the list of workspaces, an option to create a workspace, and sign out.
- **Workspace Navigation section**: The B2B Workspace section of the sidebar (`LinearSidebar`) will render **Members** (pointing to `/workspace/members`) and **Documents** (pointing to `/workspace/documents`).
- **Personal Profile Settings**: The **Settings** link is visible ONLY to users with the `Owner` role. It points to the personal user settings page (`/settings`).
- **Personal Settings Form**: The `/settings` page will feature a Linear-style Profile settings form to update personal details (Full Name, Phone, preferredLanguage, timezone) connecting to `PUT /api/v1/auth/me`.

---

## Backend Contract Source

### Profile API

Endpoint: `PUT /api/v1/auth/me`
Payload DTO: `UpdateProfileRequest`

| Field | Type | Required | Notes |
|---|---|---|---|
| `fullName` | `string?` | No | Display name |
| `phone` | `string?` | No | Contact phone |
| `preferredLanguage` | `string?` | No | Interface language (e.g. `vi-VN`) |
| `timezone` | `string?` | No | User time zone (e.g. `Asia/Ho_Chi_Minh`) |

---

## Target Web Files

| File | Change Details |
|---|---|
| `warptalk-web/src/app/workspace/page.tsx` | Redirect active workspace users to `/host/dashboard` (instead of `/workspace/dashboard`). |
| `warptalk-web/src/app/workspace/create/page.tsx` | Redirect to `/host/dashboard` on successful workspace creation. |
| `warptalk-web/src/components/layout/linear-sidebar.tsx` | Integrate dynamic workspace selection, dropdown switching list, conditional `Settings` rendering for `Owner`, and links for `Members` and `Documents`. |
| `warptalk-web/src/app/(app)/settings/page.tsx` | Implement a dark mode personal profile settings page connecting to GET/PUT /api/v1/auth/me. |

---

## Implementation Steps

### Step 1 - Update Onboarding Redirection
In `warptalk-web/src/app/workspace/page.tsx` and `warptalk-web/src/app/workspace/create/page.tsx`, update all router redirects to `/host/dashboard` upon workspace selection or creation.

### Step 2 - Modify Sidebar (`LinearSidebar`)
In `warptalk-web/src/components/layout/linear-sidebar.tsx`:
- Import `Users`, `FileText`, `SignOut` icons.
- Import `DropdownMenu` and subcomponents.
- Fetch available workspaces using `useWorkspaces` hook.
- Implement selector with dynamic initials and list of workspaces.
- Check `workspaceRole === "Owner"` dynamically.
- Update sidebar items dynamically:
  - Add "Members" (`/workspace/members`)
  - Add "Documents" (`/workspace/documents`)
  - Add "Settings" (`/settings`) if user role is `Owner`.

### Step 3 - Implement Personal Settings Page
In `warptalk-web/src/app/(app)/settings/page.tsx`:
- Fetch user profile on load.
- Render the Profile form (Full Name, Phone, timezone, preferredLanguage).
- Apply Linear styling (dark background, precise 1px borders, rounded fields).
- Handle submit API calls to `PUT /api/v1/auth/me` with feedback toasts and update Zustand store dynamically.
