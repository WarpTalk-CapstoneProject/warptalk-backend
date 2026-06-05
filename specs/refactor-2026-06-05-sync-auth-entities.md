# Refactor: Synchronize Auth Domain Entities and Keep DbContext and Entities Raw
Date: 2026-06-05
Author: Antigravity

## What is being refactored
- **`WarpTalk.AuthService.Domain` (Auth Domain project):**
  - Delete `Partials/User.Custom.cs` containing custom entity helper methods (`GetRoles`).
- **`WarpTalk.AuthService.Application` (Auth Application project):**
  - Add `Helpers/UserExtensions.cs` containing the `GetRoles` extension method to keep domain entities completely raw and free of custom helper functions.
- **Microservices Alignment:**
  - Verify alignment of `WarpTalk.TranslationRoomService`'s `UserSetting` read replica view with `auth.user_settings`.

## Why
- **Scaffold Integrity:** The database schema is the single source of truth for the Auth domain entities and the DB context. In order to cleanly support future re-scaffolding without breaking custom domain logic or wiping it out, all entity classes and `AuthDbContext` must remain 100% raw output from the DB scaffolding command.
- **Separation of Concerns:** Business-logic helpers like resolving roles from `UserRoleUsers` are relocated from the Domain Entity level to the Application layer as extension methods.
- **Microservice Sync:** Ensuring that replica models in downstream microservices are correctly synced to the DB schema fields.

## What does NOT change
- Core authentication flows and business rules.
- JWT token generation, claims, and role authorization logic.
- The `auth` schema tables and constraints in `init-db.sql`.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)?
- [x] Scaffold entities and DbContext in Auth contain zero manual modifications?
- [x] Downstream microservice replicas are in sync?
- [x] Tests still pass?
