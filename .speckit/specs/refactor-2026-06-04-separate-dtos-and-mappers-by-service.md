# Refactor: Separate DTOs and Mappers by Service
Date: 2026-06-04

## What is being refactored
Reorganizing directories, files, and namespaces for DTOs and Mappers in `WarpTalk.WorkspaceService.Application`:
- `WarpTalk.WorkspaceService.Application/DTOs` -> `Workspace/`, `WorkspaceInvitation/`, `WorkspaceMember/`
- `WarpTalk.WorkspaceService.Application/Mappers` -> `Workspace/`, `WorkspaceInvitation/`, `WorkspaceMember/`
- Splitting `WorkspaceMapper.cs` into individual service mappers: `WorkspaceMapper.cs`, `WorkspaceInvitationMapper.cs`, `WorkspaceMemberMapper.cs`.
- Updating namespaces and using directives across Application, API, and Tests projects.

## Why
Currently, all DTOs are at the root of `DTOs/` and a single `WorkspaceMapper.cs` contains mapping logic for three distinct services (Workspace, Invitation, and Member). Separating them by service improves maintainability, domain alignment, and reduces the size of the mapper file.

## What does NOT change
All business logic, API contracts, DB schema, and test expectations remain exactly the same. Only folders, namespace declarations, and using statements are refactored.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)? (Yes, Application layer internal structure only)
- [x] Communication channels unchanged (Article II)? (Yes, no changes to gRPC, Redis, or SignalR)
- [x] Tests still pass? (To be verified)
