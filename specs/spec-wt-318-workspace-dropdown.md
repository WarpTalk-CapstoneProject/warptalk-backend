# Specification Alignment: WT-318 — Workspace Dropdown Menu & Switch Workspace Submenu

- **Ticket Reference:** [WT-318](https://linear.app/fpt-sep490-su26/issue/WT-318/workspace-dropdown-implement-workspace-dropdown-menu-with-switch)
- **Module:** Workspace Module / Navigation Alignment
- **Status:** Approved / Verified
- **Author:** Nhi Ngô (@hanhnhi10022005)
- **Reviewer:** Tú Huỳnh (@huynhthaitu124)

---

## 1. Executive Summary

This specification alignment confirms that the front-end Workspace Dropdown Menu & Switch Workspace Submenu feature (WT-318) relies entirely on the existing Workspace API contracts in `WarpTalk.WorkspaceService`.

No breaking backend API changes or database migrations are required for WT-318.

---

## 2. Verified Backend API Endpoints

The existing backend endpoints consumed by the front-end workspace menu dropdown:
1. `GET /api/v1/workspaces`: Lists user's workspaces (`useWorkspaces`).
2. `POST /api/v1/workspaces/{id}/select`: Selects active workspace (`useSelectWorkspace`).
3. Auth Session: Session user state (`email`, `fullName`, `role`) is supplied via JWT/auth cookie.

---

## 3. Acceptance Verification

- [x] Backend contract alignment verified against `WorkspaceController` endpoints.
- [x] Front-end PR [#85](https://github.com/WarpTalk-CapstoneProject/warptalk-web/pull/85) connected without backend schema changes.
