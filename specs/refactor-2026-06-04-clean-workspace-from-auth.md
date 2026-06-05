# Refactor: Clean Workspace Code from AuthService and Centralize in WorkspaceService
Date: 2026-06-04

## What is being refactored
- **`WarpTalk.AuthService` (Auth Microservice):** Removing all Workspace-related controllers, services, repositories, entities, mappers, validators, and DTOs.
- **`WarpTalk.WorkspaceService` (Workspace Microservice):** Cleaning up compilation errors, introducing external `User` and `Role` non-entity representations, splitting the bloated `WorkspaceService` into individual micro-services (`WorkspaceService`, `WorkspaceMemberService`, `WorkspaceInvitationService`), and updating dependency injection.

## Why
- **Microservices Isolation:** In a microservices architecture, the Workspace microservice should be the single source of truth for workspaces, memberships, and invitations. The Auth microservice was holding onto old workspace logic, causing redundant code, database schema duplication, and cross-service architectural leakage.
- **Compilation Resolution:** The workspace microservice currently does not compile due to referencing non-existent repositories (`RoleRepository`) and domain entities (`Role`) which existed in the Auth microservice.
- **Clean Architecture & Single Responsibility Principle:** Splitting the bloated `WorkspaceService` into dedicated services simplifies testing and aligns with their respective repositories.

## What does NOT change
- Core authentication flows (JWT tokens, login, register, profile management, Google OAuth).
- The REST API endpoints for workspaces, members, and invitations (routes, request/response formats must remain identical, though they are now served by the Workspace microservice).
- The gRPC contract between WorkspaceService and AuthService.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)?
- [x] Communication channels unchanged (Article II)?
- [x] Tests still pass?
