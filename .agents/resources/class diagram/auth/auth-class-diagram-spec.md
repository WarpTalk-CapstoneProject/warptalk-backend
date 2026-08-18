# Class Diagram Specification - Auth Module

Key classes of the Auth module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `User` | `Id, Email, PasswordHash, FullName, PreferredLanguage, Timezone, IsActive, IsLocked, EmailVerified, GoogleId` | Core identity entity for authentication; tracks user credentials, security lockout states, email verification status, and linked Google OAuth identity. |
| `UserSetting` | `DefaultSpeakLanguage, DefaultListenLanguage, VoiceCloneEnabled, MicNoiseSuppression, Theme` | Per-user preference settings for personalized speech translation, noise suppression, and UI themes. |
| `Role` | `Id, Name, Description, IsSystem, IsActive` | System role entity defining authorization levels (e.g. Admin, User). |
| `Permission` | `Id, Code, Description, GroupName, IsActive` | Fine-grained permission definition entity representing atomic system capabilities. |
| `UserRole` | `Id, UserId, RoleId, AssignedAt` | Junction entity binding users to system roles for Role-Based Access Control (RBAC). |
| `RolePermission` | `RoleId, PermissionId` | Junction entity mapping permissions to system roles. |
| `RefreshToken` | `Id, UserId, FamilyId, TokenHash, DeviceInfo, IpAddress, ExpiresAt, RevokedAt` | Manages active sessions and JWT refresh rotation; uses FamilyId reuse detection to revoke compromised token chains. |
| `VoiceProfile` | `Id, UserId, WorkspaceId, DisplayName, Provider, EmbeddingRef, Language, Status` | User voice profile entity storing AI voice cloning embeddings and language metadata. |
| `VoiceSample` | `Id, VoiceProfileId, SampleType, FileUrl, DurationSeconds, Language` | Audio sample entity uploaded for voice profile training and synthesis validation. |
| `AuthController` | `Register(...), RegisterInvited(...), Login(...), VerifyEmail(...), ResetPassword(...)` | REST controller handling user registration, login, email verification, and password reset operations. |
| `GoogleAuthController` | `GoogleLogin(...), LinkGoogle(...), UnlinkGoogle(...)` | REST controller managing OAuth 2.0 Google SSO authentication and account binding. |
| `TokenController` | `Refresh(...), Logout(...)` | REST controller handling access token rotation and explicit session termination. |
| `ProfileController` | `GetProfile(), UpdateProfile(...), ChangePassword(...)` | REST controller exposing user profile management and password update endpoints. |
| `AuthService` | `RegisterAsync(...), LoginAsync(...), VerifyEmailAsync(...), ResetPasswordAsync(...)` | Application service orchestrating credential validation, account security lockouts, and password reset flows. |
| `GoogleAuthService` | `GoogleLoginAsync(...), LinkGoogleAsync(...), UnlinkGoogleAsync(...)` | Application service resolving Google OAuth tokens and linking Google identity records. |
| `TokenService` | `RefreshTokenAsync(...), LogoutAsync(...)` | Application service enforcing Refresh Token Rotation policy and revoking token families upon breach detection. |
| `UserRepository` | `GetByEmailWithRolesAsync(...), GetByIdWithRolesAsync(...)` | Persistence repository for retrieving user identities and associated roles. |
| `RefreshTokenRepository` | `GetByTokenHashAsync(...), RevokeFamilyAsync(...)` | Persistence repository managing token hash validation and token family revocations. |
| `PasswordHasher` | `Hash(...), Verify(...)` | Security component performing password hashing and verification. |
| `JwtTokenGenerator` | `GenerateToken(...)` | Infrastructure component generating signed JWT tokens with embedded identity claims. |
