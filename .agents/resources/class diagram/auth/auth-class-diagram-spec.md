# Class Diagram Specification - Auth Module

Key classes of the Auth module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `User` | `Id, Email, PasswordHash, FullName, PreferredLanguage, Timezone, IsActive, IsLocked, EmailVerified, GoogleId` | Core identity entity for authentication; tracks credentials, security lockout flags, email verification status, and linked Google OAuth identity. |
| `RefreshToken` | `Id, UserId, FamilyId, TokenHash, DeviceInfo, IpAddress, ExpiresAt, RevokedAt` | Manages session rotation and security policies; uses `FamilyId` to detect token reuse and trigger family-wide revocation. |
| `UserSetting` | `DefaultSpeakLanguage, DefaultListenLanguage, VoiceCloneEnabled, Theme` | Per-user system settings and preferences for personalizing speech translation, AI voice features, and UI display. |
| `AuthController` | `Register(...), RegisterInvited(...), Login(...), VerifyEmail(...), ResetPassword(...)` | Boundary controller exposing traditional authentication, email verification, invited user onboarding, and password reset endpoints. |
| `GoogleAuthController` | `GoogleLogin(...), LinkGoogle(...), UnlinkGoogle(...)` | Handles OAuth 2.0 authentication flow with Google, linking/unlinking Google accounts to existing user profiles. |
| `TokenController` | `Refresh(...), Logout(...)` | Manages active JWT Access Token refresh cycles and explicit session revocation (logout). |
| `ProfileController` | `GetProfile(), UpdateProfile(...), ChangePassword(...)` | Manages user profile details, language/timezone preferences, and password modifications. |
| `AuthService` | `RegisterAsync(...), LoginAsync(...), VerifyEmailAsync(...), ResetPasswordAsync(...)` | Core application service orchestrating user registration, credential validation, lockout enforcement, and password reset workflows. |
| `GoogleAuthService` | `GoogleLoginAsync(...), LinkGoogleAsync(...), UnlinkGoogleAsync(...)` | Application service resolving Google OAuth tokens into user identity records and managing OAuth account bindings. |
| `TokenService` | `RefreshTokenAsync(...), LogoutAsync(...)` | Enforces Refresh Token Rotation security policy, detecting reused tokens and revoking compromise chains. |
| `UserRepository` | `GetByEmailWithRolesAsync(...), GetByIdWithRolesAsync(...)` | Persistence repository accessing user identities and role assignments. |
| `RefreshTokenRepository` | `GetByTokenHashAsync(...), RevokeFamilyAsync(...)` | Persistence repository for validating token hashes and revoking token families on security breaches. |
| `PasswordHasher` | `Hash(...), Verify(...)` | Infrastructure security component performing cryptographic hashing (BCrypt/PBKDF2) and verification. |
| `JwtTokenGenerator` | `GenerateToken(...)` | Infrastructure component issuing signed JWT Access Tokens embedded with user identity and authorization claims. |
