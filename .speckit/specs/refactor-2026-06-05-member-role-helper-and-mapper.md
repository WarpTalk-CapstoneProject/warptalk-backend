# Refactor: Member Role Helper and Mapper
Date: 2026-06-05

## What is being refactored
- Moving `PreviewInvitationResponse` creation logic inside `WorkspaceInvitationService.cs` into `WorkspaceInvitationMapper.cs`.
- Adding extension helper methods (`IsOwner`, `IsAdmin`, `IsOwnerOrAdmin`) on `string?` in `WorkspaceMemberRoleExtensions.cs`.
- Replacing raw role string checks and `ToRoleName()` equality comparisons with these clean extension methods across:
  - `WorkspaceService.cs`
  - `WorkspaceMemberService.cs`
  - `WorkspaceInvitationService.cs`

## Why
- Centralizing response mapping improves Separation of Concerns and adheres to the mapper pattern.
- Replacing repetitive `roleName == WorkspaceMemberRole.Owner.ToRoleName() || roleName == WorkspaceMemberRole.Admin.ToRoleName()` checks with short, readable extension methods (e.g., `roleName.IsOwnerOrAdmin()`) reduces boilerplate and makes the service code much cleaner and easier to read.

## What does NOT change
- No business logic behavior is altered.
- API endpoints, contracts, error messages, and DB entities remain unchanged.
- All unit and integration tests must continue to pass without modifications.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)? (Yes, Domain extensions and Application layer mapping only)
- [x] Communication channels unchanged (Article II)? (Yes)
- [x] Tests still pass? (To be verified)
