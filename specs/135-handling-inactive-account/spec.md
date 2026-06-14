# Feature Specification: Handling Inactive and Unverified Account Authentication (WT-135)

**Feature Branch**: `feat/auth`  
**Created**: 2026-05-21  
**Status**: Approved  
**Input**: User description: "Handling inactive/disabled/locked accounts and pending unverified email flows in the backend auth service"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Block Disabled Account Login (Priority: P1)

An administrator has disabled a user's account (`Status = AccountStatus.DISABLED` or `DeletedAt is not null`). When the user attempts to log in using their email and password or Google SSO, the system must immediately block them and return an `AccountInactive` error to prevent access.

**Why this priority**: Core security requirement. Disabled accounts must never be allowed to generate active sessions.

**Independent Test**:
1. Disable a test user account (`Status = AccountStatus.DISABLED` in the database).
2. Send a POST request to `/api/v1/auth/login` with the user's valid credentials.
3. Verify that the response returns `400 Bad Request` with error code `AccountInactive` and message `"Account is deactivated"`.

**Acceptance Scenarios**:

1. **Given** a user account with `Status = AccountStatus.DISABLED`, **When** they attempt to login, **Then** the login fails and returns error code `AccountInactive`.
2. **Given** a user account with `DeletedAt is not null`, **When** they attempt to login, **Then** the login fails and returns `InvalidCredentials` (acting as if the user does not exist for security reasons).

---

### User Story 2 - Block Locked Account Login (Priority: P1)

A user account is locked (`Status = AccountStatus.LOCKED` or has exceeded `MaxFailedAttempts`). When the user attempts to log in, the system must block them and inform them when the lockout expires.

**Why this priority**: Essential to protect against brute-force attacks.

**Independent Test**:
1. Lock a test user account (`Status = AccountStatus.LOCKED`).
2. Send a POST request to `/api/v1/auth/login` with the user's valid credentials.
3. Verify that the response returns `400 Bad Request` with error code `AccountLocked` and message detailing the lockout time.

---

### User Story 3 - Block Login for Unverified (Pending) Accounts (Priority: P1)

A user registers a new account, but has not verified their email yet (`Status = AccountStatus.PENDING` or `EmailVerified = false`). When the user attempts to log in using their email and password or Google SSO, the system must strictly block the login attempt and return an `AccountPending` error to prevent access.
After receiving this error, the client-side app will direct the user to the verification screen, notifying them that their email is not verified, and provide options to request a new verification email.

**Why this priority**: Core security and workflow requirement. Ensures all active sessions are tied to confirmed, verified email addresses.

**Independent Test**:
1. Register a new user (`EmailVerified = false`, `Status = AccountStatus.PENDING`).
2. Send a POST request to `/api/auth/login` with correct credentials.
3. Verify that login **fails** with `401 Unauthorized` (or `400 Bad Request`), returning error code `AccountPending` and message `"Email not verified"`.

**Acceptance Scenarios**:

1. **Given** a user account with `Status = AccountStatus.PENDING`, **When** they attempt to login, **Then** the login fails and returns error code `AccountPending`.

---

### User Story 4 - Resend Verification Email (Priority: P2)

An unverified user clicks the "Resend Verification Email" button on their dashboard banner or verification screen. The backend generates a new verification token and dispatches a verification email.

**Why this priority**: Critical path for unverified users to transition into fully verified active members.

**Independent Test**:
1. Sign up a new user (who receives an immediate session upon successful registration).
2. Send a POST request to `/api/v1/auth/resend-verification` with the authorization header.
3. Verify that the response returns `200 OK` and a verification email is successfully queued.

---

### Edge Cases

- **User deactivated during active session**: The next `/refresh` request will hit the check and block access, returning `AccountInactive` and revoking the active token.
- **Unverified Google Login**: If a user logs in via Google and Google asserts `email_verified: true`, the account status is promoted to `AccountStatus.ACTIVE` immediately. If Google says the email is not verified, the account login fails with `AccountPending`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST check `Status == AccountStatus.DISABLED` during standard, refresh, or Google login and block authentication with `ErrorCodes.AccountInactive`.
- **FR-002**: The system MUST check `Status == AccountStatus.LOCKED` or locked-until timestamp, blocking login with `ErrorCodes.AccountLocked`.
- **FR-003**: The system MUST BLOCK successful login and token issuance for users with `Status == AccountStatus.PENDING` (or `EmailVerified == false`) by returning `ErrorCodes.AccountPending`.
- **FR-004**: The system MUST include the `EmailVerified` boolean and `Status` enum in `UserDto` within the `AuthResponse` payload (when successful).
- **FR-005**: The system MUST expose a `POST /api/auth/resend-verification` endpoint for users to request a new verification email.
- **FR-006**: The system MUST check `Status == AccountStatus.PENDING` (or `EmailVerified == false`) during token refresh and block the request, returning `ErrorCodes.AccountPending`.
- **FR-007**: The system MUST enforce a minimum 60-second cooldown between resend verification requests for the same account, returning `COOLDOWN_ACTIVE`.
- **FR-008**: The system MUST restrict attempts to 5 requests per 15-minute window for the same account to protect against mail server flooding, returning `RATE_LIMIT_EXCEEDED`.

### Key Entities

- **User**: Central entity containing `Status` (PENDING, ACTIVE, DISABLED, LOCKED) and `EmailVerified` boolean.
- **RefreshToken**: Tied to user sessions. Revoked immediately if the user is disabled, locked, or unverified.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of unverified accounts are strictly blocked from logging in.
- **SC-002**: 100% of disabled accounts are blocked immediately on all authentication gates.
- **SC-003**: The "Resend Verification" endpoint triggers a verification email queue successfully in under 2 seconds.

## Assumptions

- Google SSO verified emails bypass the `PENDING` status and are created as `ACTIVE` directly.
