# Refactor: Restore Raw Scaffold Entities and Handle Enums in Application
Date: 2026-05-21
Author: Antigravity

## What is being refactored
- **Modified Entities**:
  - `WarpTalk.AuthService.Domain/Entities/User.cs` [MODIFY]
- **New Partial Entity Extension**:
  - `WarpTalk.AuthService.Domain/Partials/User.Custom.cs` [NEW]
- **Modified Application Mapper**:
  - `WarpTalk.AuthService.Application/Mappers/AuthMapper.cs` [MODIFY]
- **Modified Application Service**:
  - `WarpTalk.AuthService.Application/Services/AuthService.cs` [MODIFY]

## Why
- **Database Entity Integrity & Re-scaffolding Safety**: The leader requested that DbContext and Entity files remain strictly 100% raw output from the DB scaffolding command. Any custom fields or methods added directly to scaffolded files will be wiped out upon subsequent schema updates/re-scaffolding.
- **Handling Enums in Application & API Layers**: The custom computed `Status` property (which calculates `AccountStatus` from `IsActive`, `IsLocked`, `LockedUntil`, `EmailVerified`) is moved out of the database entity model and into the Application layer (`AuthMapper.cs` static helper).
- **Separation of Concerns (Article I)**: DB entities only reflect physical schema states. Domain methods like `GetRoles` are partitioned into a `User.Custom.cs` partial class, keeping the scaffolded `User.cs` clean and untouched.

## What does NOT change
- Business rules (lockout limits, status checks, validation logic).
- API contracts (DTO outputs like `UserDto` containing the Enum-based `Status`).
- Database schema and original Scaffold definitions.

## Clean Architecture Compliance Check
- [x] Still follows Article I (Domain -> Application -> Infrastructure -> API)?
- [x] Scaffold entity `User.cs` contains zero manual modifications?
- [x] Domain rules remain clean in the partial class `User.Custom.cs`?
- [x] API remains fully backward compatible?
