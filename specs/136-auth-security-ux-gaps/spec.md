# Feature Specification: AuthService Security, Consistency, and UX Gaps (WT-136)

**Feature Branch**: `hotfix/auth-security` (or `feat/auth-security`)  
**Created**: 2026-05-22  
**Status**: Approved  
**Input**: Linear ticket WT-136 - Resolve security vulnerabilities, data consistency bugs, and user experience issues in the authentication service.

## 1. Problem Statement

The current authentication service has several logical gaps:
1. **Profile Access Bypass**: `ProfileService` does not check if an account is `DISABLED` or `LOCKED`. An active JWT token can bypass lockouts to fetch or alter profile details.
2. **Data Consistency in Google SSO**: If a user attempts to log in via Google SSO but their account is locked/disabled, the system blocks them *before* persisting the Google identity link (`GoogleId`) and email verification status to the database.
3. **Rate Limiting Flaws**: The `ResendVerification` email rate limiter uses a Sliding Window mechanism (resetting the 15-minute TTL on every failed attempt), which is overly aggressive.
4. **UX Gaps for Pending Users**: `PENDING` users (unverified emails) currently lack clarity on what operations they can perform.

## 2. Technical Decisions (Resolved Open Questions)

Based on engineering review, the following decisions have been finalized:

### Decision 1: `UserStatusHelper.CheckUserStatus` Overloads
- **Issue**: Need to validate status in methods returning `Result` (non-generic), like `ChangePasswordAsync`.
- **Decision**: **Do not add new non-generic overloads.** Utilize the existing generic `CheckUserStatus<bool>()` because `Result<bool>` inherits from `Result` (Liskov Substitution Principle). This keeps the helper clean.

### Decision 2: Early `SaveChangesAsync` in GoogleAuthService
- **Issue**: Persisting Google SSO identity links before rejecting a login attempt due to lockout/deactivation.
- **Decision**: Only apply the early `SaveChangesAsync` in the **existing user flow** (`else` branch) when `needsUpdate = true`. For entirely new users (`Guid.Empty`), we rely on the final `SaveChangesAsync` of the main flow.

### Decision 3: Fixed Window Payload Format for Rate Limiting
- **Issue**: Moving from Sliding Window to Fixed Window requires storing both the attempt count and the absolute expiration time in `IDistributedCache`.
- **Decision**: Use the string format `"attemptsCount|expiryIsoString"`.
- **Example**: `"3|2026-05-22T13:30:00Z"`. 
- **Delimiter**: `|`
- **Timestamp**: ISO 8601 UTC format.

### Decision 4: `PENDING` Status Access in ProfileService
- **Issue**: Should unverified (`PENDING`) accounts be blocked from using `ProfileService`?
- **Decision**: 
  - `GetProfileAsync`: **ALLOWED**. Users can view their basic profile details (e.g., to confirm information) while pending.
  - `UpdateProfileAsync` & `ChangePasswordAsync`: **BLOCKED**. Users must prove ownership of their email (by verifying it) before they can update their profile or change credentials.

## 3. Requirements

### Functional Requirements

- **FR-001**: `ProfileService.GetProfileAsync` MUST block users with `DISABLED` or `LOCKED` status, returning `AccountInactive` or `AccountLocked`. It MUST NOT block `PENDING` users.
- **FR-002**: `ProfileService.UpdateProfileAsync` and `ProfileService.ChangePasswordAsync` MUST block users with `DISABLED`, `LOCKED`, or `PENDING` status.
- **FR-003**: `GoogleAuthService` MUST persist identity links (`GoogleId`, `EmailVerified`) to the database *before* evaluating the `UserStatusHelper` blocks, for existing users.
- **FR-004**: `AuthService.ResendVerificationAsync` MUST implement a strict Fixed Window rate limiter using the `"attemptsCount|expiryIsoString"` format, preserving the original expiration time across subsequent requests within the same window.

## 4. Verification & Testing

- **ProfileServiceTests**: Introduce new unit tests verifying that `DISABLED` and `LOCKED` accounts are blocked across all endpoints, while `PENDING` accounts are blocked from `UpdateProfileAsync` and `ChangePasswordAsync` but can access `GetProfileAsync`.
- **AuthServiceTests**: Update cache payload assertions for `ResendVerificationAsync` to match the new `"attempts|timestamp"` format.
- **Manual Verification**: Update profile details with a `DISABLED` account's JWT (expect `400 AccountInactive`). Hit the resend endpoint 6 times (expect `429 TooManyRequests` on the 6th attempt without resetting the 15-minute window).
