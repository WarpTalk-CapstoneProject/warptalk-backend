# Refactor: Create AuthMapper and Extract Mapping Logic in AuthService
Date: 2026-05-21
Author: Antigravity

## What is being refactored
- **New Mapper File**:
  - `WarpTalk.AuthService.Application/Mappers/AuthMapper.cs` [NEW]
- **Refactored Service**:
  - `WarpTalk.AuthService.Application/Services/AuthService.cs` [MODIFY]

## Why
- **Clean Architecture & Separation of Concerns (Article I)**: An Application Service should focus on orchestration, business logic flow, and persistence coordination. Manual construction of DTOs from Entity models pollutes the service and introduces redundant, boilerplate code.
- **Dry Code**: In the original implementation of `AuthService.cs`, the user roles extraction logic and mapping to `UserDto` was replicated across six different methods (`RegisterAsync`, `LoginAsync`, `RefreshTokenAsync`, `GetProfileAsync`, `UpdateProfileAsync`, `GoogleLoginAsync`).
- **Standardized Codebase Design**: Placing static converters in a dedicated `Mappers/` folder aligns with pre-existing conventions in the `NotificationService`, `TranslationRoomService`, and `TranscriptService` modules.

## What does NOT change
- Business logic (lockout limits, hashing logic, validation flows).
- Public DTO API contracts (e.g. `UserDto`, `AuthResponse`).
- Database entity representations (`User`, `RefreshToken`).

## Clean Architecture Compliance Check
- [x] Still follows Clean Architecture (Domain -> Application -> Infrastructure -> API)?
- [x] Role resolution remains fully backward compatible?
- [x] Build integrity preserved?
