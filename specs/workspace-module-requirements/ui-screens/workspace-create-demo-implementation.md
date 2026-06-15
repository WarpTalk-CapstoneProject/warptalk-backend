# Workspace Create Demo Implementation Specification

## Goal

Implement the demo flow for an authenticated enterprise user who has no active workspace context. The flow starts with a full-screen Workspace Onboarding Gate with no sidebar, then focuses on Create Workspace.

This document is implementation-facing. It maps the UI screens to the current backend DTOs and current `warptalk-web` file structure.

## Resolved decisions

- The screen is named **Workspace Onboarding Gate**.
- It appears only when the authenticated user has no active workspace context.
- It must not render the workspace sidebar or topbar.
- It shows two choices: `Join workspace` and `Create workspace`.
- Demo scope focuses on `Create workspace`; `Join workspace` may route to the invitation-token flow or a placeholder.
- For create demo, the verified domain is derived from the signed-in user's email domain.
- The suggested verified domain is shown in the UI but locked/read-only for this demo flow.
- The UI must not allow the user to type an arbitrary verified domain during create because the backend currently marks submitted domains as `verified` immediately with `VerificationMethod = system`.
- If a future flow needs a different domain, it must go through Settings / Verified Domains with an explicit verification challenge.

## Backend contract source

Current backend DTOs live in:

- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/DTOs/Workspace/WorkspaceDtos.cs`
- `warptalk-backend/workspace/src/WarpTalk.WorkspaceService.Application/DTOs/Workspace/WorkspaceSettingsDto.cs`

### Create request

`CreateWorkspaceRequest`:

| Field | Type | UI source | Demo rule |
|---|---|---|---|
| `name` | `string` | Form input | Required, min 2 chars. |
| `logoUrl` | `string?` | Optional form input | Optional URL; can be empty/null. |
| `verifiedDomains` | `List<string>?` | Derived from authenticated user email domain | Send `[emailDomain]` for enterprise create demo. Do not let user edit in this demo. |
| `requireVerifiedDomainForInternal` | `bool?` | Fixed policy for demo | Send `true` for enterprise create demo. |

Do not submit `slug`. The current backend generates it from `name` through `SlugHelper.GenerateSlug` and resolves collisions server-side.

### Create response

`WorkspaceDto`:

| Field | Type | UI usage |
|---|---|---|
| `id` | `Guid` | Select newly created workspace and store active context. |
| `name` | `string` | Success summary, workspace store. |
| `slug` | `string` | Success summary, workspace store. |
| `logoUrl` | `string?` | Success summary/avatar fallback. |
| `role` | `string` | Should be `Owner`; store as active role. |
| `createdAt` | `DateTime` | Optional success metadata. |

### Follow-up select request

After successful create, call:

`POST /api/v1/workspaces/{id}/select`

Then persist active workspace in `useWorkspaceStore.setActiveWorkspace(id, name, slug, role, "Internal")` and route to `/workspace/dashboard`.

## Verified domain behavior

### Why the domain must be locked in create demo

The current backend behavior is:

1. If `verifiedDomains` is provided, backend uses that list.
2. Backend defaults `requireVerifiedDomainForInternal` to `true` when domains are provided.
3. Backend rejects public domains.
4. Backend rejects domains already verified by another active workspace.
5. Backend creates `WorkspaceVerifiedDomain` rows with `Status = "verified"` and `VerificationMethod = "system"`.
6. Backend creates the creator as Owner/Internal.

Because step 5 system-verifies immediately, allowing the user to edit `verifiedDomains` would let `owner@enterprise.vn` create a workspace verified for `other-company.vn` if that domain is non-public and not already registered. That would break the meaning of Internal Home Workspace.

### Demo rule

For the create demo:

- Parse the email domain from `useAuthStore().user.email`.
- Show it as `Verified domain`.
- Mark it as read-only or locked.
- Explain with concise copy: "This domain comes from your signed-in business email."
- Submit `verifiedDomains: [emailDomain]`.
- Submit `requireVerifiedDomainForInternal: true`.

### Blocked cases

| Case | UI behavior |
|---|---|
| User email missing | Disable create and show account identity error. |
| User email invalid | Disable create and show account identity error. |
| User email uses public domain | Disable domain-verified create and show "Use a business email or join by invitation." |
| Backend returns `DomainRegisteredElsewhere` | Show inline domain conflict near verified domain field. |
| Backend returns `UserAlreadyInternalElsewhere` | Show Internal Home Workspace conflict near form summary. |

## Target web files

| File | Required change |
|---|---|
| `warptalk-web/src/app/workspace/layout.tsx` | Treat onboarding routes as no-sidebar full-screen routes. |
| `warptalk-web/src/app/workspace/page.tsx` | Replace the current mixed list/create page with the Workspace Onboarding Gate. The no-active-context demo path must not show a workspace list/table. |
| `warptalk-web/src/app/workspace/create/page.tsx` | Add focused create workspace screen for demo. |
| `warptalk-web/src/app/workspace/join/page.tsx` | Add placeholder or join-by-token entry for the second gate action. |
| `warptalk-web/src/types/workspace.ts` | Add `CreateWorkspaceRequest` type matching backend. |
| `warptalk-web/src/services/workspace.service.ts` | Use `CreateWorkspaceRequest` type for `WorkspaceService.create`. |
| `warptalk-web/src/hooks/use-workspace.ts` | Keep `useCreateWorkspace`; ensure invalidation happens after create. |
| `warptalk-web/src/stores/workspace-store.ts` | Confirm `setActiveWorkspace` stores id/name/slug/role/membershipType. |

## Route behavior

### `/workspace`

Purpose: Workspace Onboarding Gate.

Conditions:

- If auth is not ready, show full-screen loading.
- If not authenticated, redirect to login.
- If `activeWorkspaceId` exists, redirect to `/workspace/dashboard`.
- If authenticated and no active workspace context, show the full-screen gate.

Primary actions:

- `Join workspace` -> `/workspace/join`
- `Create workspace` -> `/workspace/create`

Visual rules:

- Full viewport.
- No sidebar.
- No topbar.
- Operational B2B UI, not marketing hero.
- Show current signed-in email.
- Use two equal action panels or buttons with concise labels.

### `/workspace/create`

Purpose: focused create workspace form.

Conditions:

- Same full-screen no-sidebar shell as `/workspace`.
- Requires authenticated user.
- If `activeWorkspaceId` exists, redirect to `/workspace/dashboard` unless user explicitly opens create from an existing workspace manager action later. Demo scope does not include that manager action.

Form fields:

| UI field | Backend field | Editable | Notes |
|---|---|---:|---|
| Workspace name | `name` | Yes | Required. |
| Logo URL | `logoUrl` | Yes | Optional; hidden behind optional section if needed. |
| Verified domain | `verifiedDomains[0]` | No | Derived from auth email domain. |
| Require verified domain | `requireVerifiedDomainForInternal` | No | Fixed `true`; show as enabled policy row, not a toggle. |
| Slug preview | none | No | Derived client-side only as preview; backend is authority. |

Submit payload:

```ts
{
  name: values.name.trim(),
  logoUrl: values.logoUrl?.trim() || null,
  verifiedDomains: [emailDomain],
  requireVerifiedDomainForInternal: true
}
```

Success sequence:

1. Disable submit.
2. Call `WorkspaceService.create`.
3. Receive `WorkspaceDto`.
4. Call `WorkspaceService.select(response.id)`.
5. Store active workspace with returned `id`, `name`, `slug`, `role`, and `"Internal"`.
6. Invalidate workspace queries.
7. Show success toast.
8. Route to `/workspace/dashboard`.

Error sequence:

1. Keep form values.
2. Map backend validation to local fields where possible.
3. Show field-level domain error for public/duplicate domain.
4. Show form-level conflict for Internal Home Workspace conflict.
5. Keep submit enabled after mutation settles.

## Implementation steps

### Step 1 - Add request type

In `warptalk-web/src/types/workspace.ts`, add:

```ts
export interface CreateWorkspaceRequest {
  name: string;
  logoUrl?: string | null;
  verifiedDomains?: string[];
  requireVerifiedDomainForInternal?: boolean;
}
```

Keep `WorkspaceDto` unchanged because it already matches backend response.

### Step 2 - Type the service

In `warptalk-web/src/services/workspace.service.ts`:

- Import `CreateWorkspaceRequest`.
- Change `WorkspaceService.create` argument type to `CreateWorkspaceRequest`.
- Remove any client-side `slug` from create payloads.

### Step 3 - Add email-domain helper

Create:

`warptalk-web/src/features/workspace/lib/email-domain.ts`

Rules:

- `getDomainFromEmail(email: string): string | null`
- Lowercase and trim.
- Reject missing `@`.
- Reject public domains matching backend-known public providers used in UI validation.
- Return domain only.

For demo, keep the public-domain list aligned with current frontend validation:

```ts
["gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "live.com", "aol.com"]
```

### Step 4 - Update workspace layout shell

In `warptalk-web/src/app/workspace/layout.tsx`:

- Replace `const isSelectionPage = pathname === "/workspace";`
- Use `const isOnboardingRoute = pathname === "/workspace" || pathname === "/workspace/create" || pathname === "/workspace/join";`
- Do not redirect onboarding routes when `activeWorkspaceId` is missing.
- Render onboarding routes in a full-screen shell with no `WorkspaceSidebar` and no `Topbar`.

### Step 5 - Replace `/workspace` with gate

In `warptalk-web/src/app/workspace/page.tsx`:

- Remove the current create form from this page for demo scope.
- Render the gate only.
- Read user from `useAuthStore`.
- Read active workspace from `useWorkspaceStore`.
- Use `router.push("/workspace/create")` for Create.
- Use `router.push("/workspace/join")` for Join.

Gate content:

- Title: `Set up your workspace`
- Account line: signed-in email.
- Action 1: `Join workspace`
- Action 2: `Create workspace`
- No table in the zero-active-context demo path.

### Step 6 - Add `/workspace/create`

Create `warptalk-web/src/app/workspace/create/page.tsx`.

Use:

- `react-hook-form`
- `zod`
- `useCreateWorkspace`
- `useSelectWorkspace`
- `useWorkspaceStore`
- `useAuthStore`
- `sonner`

Validation:

```ts
const schema = z.object({
  name: z.string().trim().min(2).max(100),
  logoUrl: z.string().url().optional().or(z.literal(""))
});
```

Do not include editable `domain` in the schema for demo. Domain comes from auth email.

### Step 7 - Render backend DTO preview

The page should show a compact contract/review panel so the demo makes backend fields visible.

Before submit:

- `name`: form value.
- `logoUrl`: form value or `null`.
- `verifiedDomains`: `[emailDomain]`.
- `requireVerifiedDomainForInternal`: `true`.
- `slug`: preview only, labelled "Generated by backend".

After success:

- `id`
- `name`
- `slug`
- `logoUrl`
- `role`
- `createdAt`

### Step 8 - Submit create and select

Submit handler must:

```ts
const workspace = await createWorkspace.mutateAsync({
  name: values.name.trim(),
  logoUrl: values.logoUrl?.trim() || null,
  verifiedDomains: [emailDomain],
  requireVerifiedDomainForInternal: true
});

await selectWorkspace.mutateAsync(workspace.id);
setActiveWorkspace(workspace.id, workspace.name, workspace.slug, workspace.role || "Owner", "Internal");
router.push("/workspace/dashboard");
```

### Step 9 - Add `/workspace/join`

Create `warptalk-web/src/app/workspace/join/page.tsx` as a demo-safe placeholder if full join flow is not ready.

Minimum behavior:

- Full-screen no-sidebar layout.
- Link back to `/workspace`.
- Explain that invitation links open through `/invitations/[token]`.
- Optional token input can route to `/invitations/{token}` if token is pasted.

### Step 10 - Error mapping

Map known backend messages/codes into UI locations:

| Backend condition | UI location |
|---|---|
| workspace name required | Workspace name field |
| invalid user email | Form-level account identity alert |
| cannot verify public domain | Verified domain row |
| domain registered elsewhere | Verified domain row |
| user already internal elsewhere | Form-level Internal Home Workspace conflict |
| owner role not found | Form-level platform configuration error |
| unexpected error | Form-level retry alert |

### Step 11 - Demo acceptance checklist

- Login as `owner@enterprise.vn`.
- Open `/workspace` with no active workspace.
- Confirm no sidebar/topbar.
- Confirm two actions: Join workspace and Create workspace.
- Click Create workspace.
- Confirm verified domain shows `enterprise.vn` and is not editable.
- Confirm slug is preview-only and not sent.
- Submit valid name.
- Confirm request payload sends `name`, `logoUrl`, `verifiedDomains: ["enterprise.vn"]`, `requireVerifiedDomainForInternal: true`.
- Confirm create response displays or uses `id`, `name`, `slug`, `logoUrl`, `role`, `createdAt`.
- Confirm app selects new workspace and navigates to `/workspace/dashboard`.
- Confirm dashboard now renders with sidebar/topbar.
- Repeat with duplicate domain and confirm inline domain conflict.
- Repeat with account already Internal elsewhere and confirm Internal Home Workspace conflict.

## Non-goals for demo

- Custom verified domain entry during create.
- DNS TXT verification.
- Email challenge verification.
- Admin-approved alternate domain verification.
- Multi-domain workspace creation.
- Workspace list/search redesign after the gate.
- Full join workflow beyond invitation-token routing.
