-- Backfill: the workspace owner may create meetings in their own workspace.
--
-- WHY ANY ROW IS WRONG
--   workspace.workspace_members.can_create_meetings is declared DEFAULT true, but no INSERT ever
--   reached that default. WorkspaceMemberMapper built every member entity without assigning the
--   property, and the EF model declared HasDefaultValue(true) — which makes true the property's
--   SENTINEL. EF omits a column from the INSERT only while the property still equals its sentinel,
--   so the unassigned CLR default false was written EXPLICITLY on every row. Declaring the default
--   as true is precisely what stored false.
--
--   Result: every workspace owner created through the product, and everyone who joined by
--   accepting an invitation, carries false and is refused meeting creation with a 403 in a
--   workspace they created or were invited to.
--
-- WHY THIS ONLY TOUCHES OWNERS
--   false is ambiguous. It is what the broken INSERT wrote, and it is also what an Owner/Admin
--   writes deliberately through PATCH /workspaces/{id}/members/{userId}
--   (WorkspaceMemberService.UpdateMemberAsync). Nothing in the row distinguishes the two, so a
--   blanket backfill would silently restore permissions somebody had taken away on purpose.
--
--   The workspace's own owner is the one case where false can never have been intended: the
--   product offers no way to revoke meeting creation from the owner, and an owner locked out of
--   their own workspace is the reported defect. Non-owner rows are left alone; from this release
--   onward they are created with true, and any pre-existing member can be re-granted from the
--   Members screen.
--
-- Idempotent and forward-only: re-running changes nothing once the rows read true.

UPDATE workspace.workspace_members AS m
SET can_create_meetings = true
FROM workspace.workspaces AS w
WHERE m.workspace_id = w.id
  AND m.user_id = w.owner_id
  AND m.can_create_meetings = false;
