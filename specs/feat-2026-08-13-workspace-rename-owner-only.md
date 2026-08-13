# Plan — Rename workspace (Owner-only, slug bất biến)

- **Branch**: `feature/workspace-rename-owner-only` (backend + web, cùng tách từ `origin/development` mới nhất)
- **Ngày**: 2026-08-13
- **Trạng thái**: code đã viết ở working tree, chưa commit — chờ duyệt plan

---

## 1. Problem statement

Workspace không đổi tên được. Không phải "đổi được nhưng sai quyền" — mà là **không tồn tại đường
nào để đổi**:

- `WorkspacesController` chỉ có create / list / get / select / settings (GET, PUT, PATCH) / delete.
  Không có action nào ghi `Name`.
- `WorkspaceSettingsDto` (`DTOs/Workspace/WorkspaceSettingsDto.cs:5-18`) không chứa `Name` lẫn
  `Slug`, nên endpoint settings về nguyên tắc không thể rename.
- `IWorkspaceService` không có `UpdateWorkspaceAsync` / `RenameAsync`. `Name` chỉ được gán đúng một
  lần lúc create, qua `WorkspaceMapper.ToEntity` (`Mappers/Workspace/WorkspaceMapper.cs:36-43`).
- Admin hệ thống cũng không rename được: `AdminWorkspacesController` chỉ có
  GetDirectory / GetDetail / suspend / reactivate.
- Frontend `[workspaceSlug]/settings/page.tsx:443-500` hiển thị Slug read-only, **không có field Name**.

Tên workspace đặt sai lúc tạo là vĩnh viễn.

---

## 2. Câu hỏi thiết kế thật sự: slug có gen lại theo name không?

Đây là điểm quyết định của cả tính năng. Câu trả lời: **KHÔNG.**

### 2.1 Slug hiện là primary lookup key, không phải chuỗi hiển thị

- Toàn bộ route sản phẩm nằm dưới `src/app/(app)/[workspaceSlug]/` — home, rooms, dashboard,
  documents, members, billing, settings, advanced, ai-chat, …
- Luồng join tra workspace **bằng slug**, không bằng id:
  `WorkspaceInvitationService.cs:668-676` (`w.Slug == command.WorkspaceSlug`).
- Slug hiện **bất biến**: chỉ sinh lúc create (`WorkspaceService.cs:165-166`), không nơi nào ghi lại.

### 2.2 Ba hậu quả nếu gen lại slug

**H-1 — Slug squatting (bảo mật, không phải UX).**
Rename giải phóng slug cũ. Workspace khác chiếm được. Người cầm slug cũ gửi join request sẽ
**đi thẳng vào workspace của người lạ** (`WorkspaceInvitationService.cs:668`). Không có bảng reserved
hay redirect để chặn.

**H-2 — Đá văng toàn bộ thành viên.**
Cookie `active_workspace_slug` (TTL 7 ngày, `stores/workspace-store.ts:56-60`) và localStorage
`warptalk-workspace` (`:106-113`) của *mọi member trên mọi thiết bị* giữ slug cũ.
`[workspaceSlug]/layout.tsx:60-66` không match slug → `clearActiveWorkspace()` + redirect
`/workspace`. Ai đang họp thì rơi khỏi phòng (`workspace-routes.ts:20-26`).

**H-3 — Rename có thể giết vĩnh viễn một workspace.**
`SlugHelper.IsValidSlug` (`:66-82`) và `WorkspaceSystemSettings.ReservedSlugs` (`:7`) là
**dead code — không nơi nào gọi**. Rename thành "settings" / "admin" / "rooms" sẽ sinh slug đụng
route Next.js; frontend `normalizeWorkspaceSlug` trả `null` → workspace không mở được nữa.

Ngoài ra: email "join request approved" nhúng `{appBaseUrl}/{slug}/home`
(`WorkspaceInvitationEmailComposer.cs:80`) sẽ chết vĩnh viễn, và bản sao slug đóng băng trong outbox
(`WorkspaceOutboxDelivery.cs:124`) không bao giờ được cập nhật.

### 2.3 Quyết định

**Rename chỉ đổi `Name`. `Slug` không đổi.** Giống Linear và GitHub org: đổi tên hiển thị và đổi
địa chỉ là hai hành động khác nhau. Nếu sau này thật sự cần đổi slug, đó là spec riêng và bắt buộc
kèm: bảng `workspace_slug_aliases` + redirect 301, validate reserved ở backend, partial unique index
theo `deleted_at`, và invalidate cookie/localStorage.

---

## 3. Quyết định về quyền

**Owner-only**, không phải Owner-or-Admin.

`WorkspaceMemberRole` (`Domain/Enums/WorkspaceMemberRole.cs:6-11`) đã tách `Owner` khỏi `Admin`,
và đã có sẵn helper `IsOwner()` (`Domain/Extensions/WorkspaceMemberRoleExtensions.cs:25`). Đã có hai
tiền lệ owner-only để bám theo: `WorkspaceService.cs:404-408` (policy settings) và `:516-520`
(soft delete).

**Không tạo authorization attribute/policy mới.** Codebase kiểm tra quyền workspace theo kiểu
imperative trong từng service method; policy duy nhất tồn tại là `SystemAdminAuthorization` và nó
dành cho system admin. Thêm attribute riêng cho một endpoint là lệch pattern.

**Enforce hai lớp**: backend chặn thật (nguồn sự thật), frontend gate cho đúng trải nghiệm.

---

## 4. Quyết định về vị trí UI

Form rename đặt ở **`[workspaceSlug]/advanced/page.tsx`**, không phải trang settings.

Lý do: trang `advanced` đã gate owner-only ở cấp trang (`:20, :34, :39` — cùng chỗ với transfer
ownership và delete). Trang `settings` gate ở mức owner/admin, nên đặt rename ở đó buộc phải gate lẻ
từng field — hai mức quyền trong một trang, dễ sai khi bảo trì.

Card rename đặt **trước** Danger Zone, vì rename là hành động nhẹ nhất trong nhóm.

Dùng **nút Save tường minh**, không dùng `useAutoSaveQueue` như các setting khác: tên workspace hiển
thị cho toàn bộ thành viên, auto-save theo từng ký tự gõ là sai về nghiệp vụ.

---

## 5. Thay đổi

### 5.1 Backend — `warptalk-backend/workspace/`

| File | Thay đổi |
|---|---|
| `Application/DTOs/Workspace/WorkspaceDtos.cs` | `RenameWorkspaceRequest(string Name)`, kèm doc comment ghi rõ vì sao slug bất biến. |
| `Domain/Constants/WorkspaceConstants.cs` | `MaxWorkspaceNameLength = 150`; error `OnlyOwnerCanRenameWorkspace`, `WorkspaceNameTooLong`. |
| `Application/Interfaces/IWorkspaceService.cs` | `RenameWorkspaceAsync`. |
| `Application/Services/WorkspaceService.cs` | Cài đặt (xem §5.2). |
| `API/Controllers/WorkspacesController.cs` | `PATCH api/v1/workspaces/{id:guid}/name` → `204`. |
| `tests/.../WorkspaceServiceTests.cs` | 6 test (xem §6). |

### 5.2 Thứ tự kiểm tra trong `RenameWorkspaceAsync`

1. Trim `Name`; rỗng → `ValidationError`.
2. Dài hơn 150 → `ValidationError`.
3. Workspace không tồn tại **hoặc `DeletedAt != null`** → `NotFound`.
4. Caller không phải active member (`RemovedAt == null`) → `Forbidden`.
5. `!execRoleName.IsOwner()` → `Forbidden` / `OnlyOwnerCanRenameWorkspace`.
6. Set **`Name`, `UpdatedAt`, `UpdatedBy`**. Không đụng `Slug`.

Trả `Result.Failure(...)`, không throw — bám theo `WorkspaceService.cs:404-408` và `:516-520`.

### 5.3 Web — `warptalk-web/`

| File | Thay đổi |
|---|---|
| `src/lib/api/endpoints.ts` | `workspaces.name(id)`. |
| `src/services/workspace.service.ts` | `rename(id, name)` → PATCH. |
| `src/hooks/use-workspace.ts` | `useRenameWorkspace(workspaceId)`; invalidate `WORKSPACE_KEYS.detail` + `["workspaces","list"]`. |
| `src/app/(app)/[workspaceSlug]/advanced/page.tsx` | Card "Workspace name" + nút Save; sau khi thành công gọi `setActiveWorkspace(...)` để sidebar/switcher đổi tên ngay. |

`settings/page.tsx` **không sửa**. Slug read-only ở đó giữ nguyên.

`setActiveWorkspace` được truyền lại `activeWorkspaceSlug` **không đổi** — đây là chỗ dễ vô tình
làm hỏng nhất, nên có comment tại chỗ.

---

## 6. Test

Trong `WorkspaceServiceTests`:

1. Owner rename → thành công **và `Slug` không đổi** (test quan trọng nhất, khoá ràng buộc §2.3).
2. Admin → `Forbidden`.
3. Member → `Forbidden`.
4. Tên rỗng/whitespace → `ValidationError`.
5. Tên > 150 ký tự → `ValidationError`.
6. Workspace không tồn tại → `NotFound`.

---

## 7. Ngoài scope (cố ý)

- **Không** đổi slug, **không** bảng alias, **không** migration, **không** đụng `SlugHelper`.
- **Không** sửa `WorkspaceDbContext.cs` (giữ nguyên raw scaffold theo quy ước dự án).
- **Không** thêm event `workspace.renamed` — `WorkspaceEventTypes` chưa có, và không consumer nào
  đang cần.

## 8. Nợ kỹ thuật phát hiện được, **không** xử lý ở đây

Ba thứ dưới đây là bug có sẵn, không do rename gây ra và không bị rename làm tệ hơn (vì slug bất
biến). Ghi lại để mở ticket riêng:

- **N-1**: `IsValidSlug` / `ReservedSlugs` là dead code → ngay lúc *create*, đặt tên "Settings" đã
  sinh slug đụng route.
- **N-2**: `ResolveSlugCollisionAsync` (`SlugHelper.cs:57`) không lọc `DeletedAt == null`, và unique
  index trên `slug` không phải partial index → workspace đã xoá vẫn chiếm slug vĩnh viễn.
- **N-3**: `SlugHelper` dùng `FormD` + loại `NonSpacingMark`, nhưng `đ`/`Đ` không decompose được nên
  biến thành `-`. "Nhóm Đồ Án" → `nhom-o-an`.
