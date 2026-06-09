# Feature Specification: Google OAuth Sign-In & Account Linking Rules (WT-137)

**Feature Branch**: `feat/auth-google-oauth-linking`  
**Created**: 2026-05-22  
**Status**: Approved  
**Input**: Linear ticket WT-137 - [Auth] Complete Google OAuth sign-in and account linking rules

---

## 1. Problem Statement

While Google SSO registration and sign-in are partially implemented, several key business rules, security boundaries, and edge cases around social logins and account linking remain undefined:
1. **Silent Account Takeover Vulnerability**: When a user logs in via Google with a verified email address, automatically linking it to an existing local account that has *not* verified its email address (`EmailVerified = false`) introduces a silent takeover risk. If an attacker pre-registers a local account using a victim's email address, the victim's Google SSO action could bind their Google identity to the attacker's pre-registered account, allowing the attacker to access the account via password login.
2. **Authentication Method Orphanage**: Users must not be allowed to unlink their Google account (or other social identities) if doing so leaves them with zero active authentication methods (e.g., they have no password set and no other linked OAuth providers), which would permanently lock them out.
3. **Architecture Extensibility Boundary**: The current design uses a static `GoogleId` column directly on the `User` entity. This does not scale well if other providers (e.g., Apple, Microsoft, GitHub) are added. We need a clear, future-proof logical boundaries document for provider/account identities.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Provider Identity Extensibility (Future-Proofing)
While the database schema currently utilizes the `GoogleId` column directly inside the `User` table, we will establish the following architectural boundaries to ease future extension:
- **Logical Abstraction**: In the Application layer, social sign-in verification and login resolution should be handled via abstract provider handlers (e.g., an `IExternalAuthProvider` interface).
- **Future DB Schema Transition**: Future iterations of the system will migrate to a dedicated `UserLogin` or `UserIdentity` table to support a **1-to-Many** relationship between users and authentication providers (e.g., `UserId`, `ProviderName` ("Google", "Apple"), `ProviderKey` (Subject ID), `CreatedAt`).
- **Scope for Sprint 2**: To maintain backward compatibility and avoid massive schema refactoring, the database table will continue to use `GoogleId`. However, the logic in `GoogleAuthService` and new linking endpoints will be isolated to make future transition to `UserIdentities` straightforward.

### 2.2. Safe Account Matching (takeover prevention)
- **Constraint**: If an incoming Google token contains a verified email (`EmailVerified = true`), but an existing account in our database with the same email has `EmailVerified = false` (a `PENDING` state user who signed up via Email/Password but never clicked the verification link), **automatic linking is BLOCKED**.
- **Alternative**: The system will reject the Google SSO request with a distinct error code (`EmailVerificationRequired`). The user must first verify their email using the local registration verification link, or log in via their local password to verify ownership before Google SSO linking is permitted.
- **Auto-Linking Eligibility**: Automatic account matching and linking during Google login is only permitted if the existing user record is already active and verified (`EmailVerified = true`).

### 2.3. Minimum Active Authentication Rules
- **Rule**: A user must retain at least one usable authentication method at all times.
- **Usable Methods** are defined as:
  - Local Password credential (`PasswordHash` is not null/empty).
  - Linked External Provider (e.g., `GoogleId` is not null/empty).
- **Unlinking Guard**: The `UnlinkGoogle` API will check if the user has a local password (`PasswordHash != null`). If they do not have a password set, the request to unlink Google is rejected with `MinAuthMethodRequired`.

---

## 3. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - Safe Account Linking during Google Login (Priority: P1)

*As an existing WarpTalk user with a verified local account, I want to sign in with Google seamlessly, so that my Google profile is safely linked without creating a duplicate account.*

**Why this priority**: Core SSO capability that ensures frictionless login experience for existing users while maintaining maximum security.

**Independent Test**: Can be verified by registering a local user, verifying the email via the registration token, then executing a Google login request with the matching verified email, yielding a successfully linked user with `GoogleId` updated.

**Acceptance Scenarios**:
1. **Given** a local user exists with email `alex@warptalk.vn` and `EmailVerified = true`,  
   **When** a Google sign-in request is received with email `alex@warptalk.vn` and Google verified token,  
   **Then** the system automatically associates the `GoogleId` (Subject) to the existing user and returns a successful `AuthResponse`.

2. **Given** a local user exists with email `alex@warptalk.vn` and `EmailVerified = false` (Pending Verification),  
   **When** a Google sign-in request is received with email `alex@warptalk.vn`,  
   **Then** the system **REJECTS** the request with error code `EmailVerificationRequired`, does **not** link the account, and **automatically sends a new verification email** (subject to standard cooldown/rate limits) to the user's email.

---

### User Story 2 - Account Linking/Unlinking API for Authenticated Users (Priority: P2)

*As an authenticated WarpTalk user, I want to link or unlink my Google account from my settings page, so that I can control my authentication options.*

**Why this priority**: Important for user autonomy and password management. Meets the requirement that linking/unlinking rules must be fully defined and verified.

**Independent Test**: Execute Link Google and Unlink Google API requests on behalf of a logged-in user and assert DB updates and validation blocks.

**Acceptance Scenarios**:
1. **Given** a logged-in user who does **not** have a Google account linked (`GoogleId = null`),  
   **When** they send a `POST /api/auth/google/link` request with a valid Google `idToken`,  
   **Then** the system updates their `GoogleId` in the database and returns a success response.

2. **Given** a logged-in user who already has a Google account linked (`GoogleId != null`) and has a local password set (`PasswordHash != null`),  
   **When** they send a `POST /api/auth/google/unlink` request,  
   **Then** the system nullifies `GoogleId` in the database and returns a success response.

3. **Given** a logged-in user who has a Google account linked (`GoogleId != null`) but has **no** local password set (`PasswordHash == null`),  
   **When** they send a `POST /api/auth/google/unlink` request,  
   **Then** the system **REJECTS** the request with error code `MinAuthMethodRequired` (Cannot remove the only authentication method).

---

### Edge Cases
- **Token Tampering / Invalid Token**: Handled by rejecting incoming Google logins/links with `InvalidToken` (status code 400).
- **Google Email Not Verified**: If Google ID Token contains `email_verified = false`, the login/linking action is rejected.
- **Account Deactivation/Suspension**: If a user attempts to log in via Google SSO and the matched account is `DISABLED` or `LOCKED`, the block must be evaluated correctly. (Handled via WT-136, but needs regression verification).

---

## 4. Requirements

### Functional Requirements
- **FR-137-001**: Google Login MUST only link to an existing local account if the local account's `EmailVerified` is `true`.
- **FR-137-002**: If email matches an existing local account but `EmailVerified` is `false`, Google Login MUST reject the request with `EmailVerificationRequired` (Error code: `EmailNotVerified`) and MUST automatically trigger a new verification email with a verification token to the user (subject to standard cooldown/rate limits).
- **FR-137-003**: The system MUST refuse to process Google Login if the token's `email_verified` claim is `false`.
- **FR-137-004**: System MUST provide an endpoint `POST /api/auth/google/link` allowing authenticated users to link their active account with a Google account.
  - Request DTO: `LinkGoogleRequest { IdToken }`
- **FR-137-005**: System MUST provide an endpoint `POST /api/auth/google/unlink` allowing authenticated users to unlink their Google account.
- **FR-137-006**: The unlinking API MUST reject the request if the user does not have a local password set (`PasswordHash` is null or empty) and has no other authentication methods.
  - Error message: `Cannot unlink Google account without a local password set.`
  - Error code: `MinAuthMethodRequired`
- **FR-137-007**: When unlinking Google, only the `GoogleId` is cleared. Personal details such as `FullName` and `AvatarUrl` MUST remain intact.

---

## 5. Success Criteria & Metrics

### Measurable Outcomes
- **SC-137-001**: **Zero Silent Account Takeovers**: Security unit and integration tests successfully prove that a pending unverified account cannot be linked automatically via Google SSO login.
- **SC-137-002**: **No Lockouts from Unlinking**: Attempting to unlink a Google account when no password hash exists is 100% blocked under test scenarios.
- **SC-137-003**: **API Response Latency**: The new endpoints `/api/auth/google/link` and `/api/auth/google/unlink` return responses in less than 500ms on average (excluding external Google API network verification latency).

---

## 6. Assumptions
- The frontend handles the initial Google authentication flow (using Google 3P SDK) to acquire a valid ID Token and sends this token to the backend APIs.
- The `IGoogleTokenVerifier` service functions correctly and securely verifies the payload authenticity.
- High-level provider extensions (e.g., Microsoft, Apple) are planned for Future Sprints and will reuse the validation patterns established in this spec.
