# Implementation Plan: Step-up Re-authentication

**Branch**: `feat/step-up-reauthentication` | **Date**: `2026-07-28` | **Spec**: `specs/162-step-up-reauthentication/spec.md`
**Input**: Feature specification for step-up re-authentication and OTP fallback.

## Summary

Add a purpose-bound re-authentication layer for sensitive actions only. The Auth service will verify password, Google ID token, or email OTP depending on account type and issue a short-lived reauth proof token that protected services can validate locally before allowing high-risk operations.

## Technical Context

**Language/Version**: .NET 10  
**Primary Dependencies**: ASP.NET Core, JWT, Redis, Resend, existing shared auth helpers  
**Storage**: PostgreSQL for primary data, Redis for OTP challenges and short-lived reauth state  
**Testing**: xUnit, integration tests with Testcontainers  
**Target Platform**: Backend API services  
**Project Type**: Multi-service web backend  
**Performance Goals**: Reauth flow should stay fast enough for modal-driven sensitive actions, with OTP delivery and proof verification completing in a few seconds under normal load  
**Constraints**: Keep reauth separate from login/refresh, preserve existing access JWT behavior, return `ProblemDetails` for failures, use `/api/v1/` routes, avoid leaking secrets or OTP values in logs  
**Scale/Scope**: Auth, shared auth middleware/contracts, and protected sensitive endpoints across Workspace and Billing

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Clean boundaries preserved: domain rules stay out of controllers, orchestration stays in application services, infrastructure handles Redis/Resend/JWT concerns.
- Communication rules preserved: no new service-to-service HTTP path is introduced for reauth validation; protected services validate proof locally.
- Platform rules preserved: .NET 10, `/api/v1/`, `ProblemDetails`, and environment-based secrets only.
- Test-first plan preserved: contract and integration tests are defined before implementation work starts.
- Security rules preserved: proof is short-lived, purpose-scoped, and bound to the authenticated account/session context.

## Project Structure

### Documentation for this feature

```text
specs/162-step-up-reauthentication/
├── plan.md
├── spec.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
└── tasks.md
```

### Source Code

```text
auth/
├── Application/
├── Domain/
├── Infrastructure/
└── Api/

shared/
├── Auth/
└── Security/

workspace/
└── Api/

billing/
└── Api/

tests/
├── auth/
├── shared/
└── protected-endpoints/
```

**Structure Decision**: Keep the implementation centered in `auth` for proof issuance and OTP challenge management, add reusable proof validation primitives in `shared`, and update protected endpoints in `workspace` and `billing` to require and validate reauth for sensitive actions only.

## Implementation Notes

- Add reauth options discovery, verification, and OTP challenge endpoints under Auth.
- Support three reauth methods: password for local accounts, Google ID token for linked Google accounts, and email OTP fallback.
- Issue a dedicated reauth proof token with a 5 minute TTL and purpose claim.
- Store OTP challenges in Redis with rate limits, attempt limits, resend limits, and one active challenge per purpose/session scope.
- Enforce reauth on sensitive actions such as account credentials/provider changes, ownership/admin changes, domain governance, billing money movement, and destructive document/voice-profile deletion.
- Keep routine operational actions out of the reauth gate unless explicitly classified as sensitive in the spec.

## Test Plan

- Contract tests for reauth endpoints, status codes, and `ProblemDetails` payloads.
- Unit tests for method selection, proof issuance, purpose matching, expiry, and OTP challenge invalidation.
- Integration tests with Redis Testcontainers and a Resend stub to verify challenge lifecycle and delivery failure handling.
- Protected endpoint tests to confirm missing, expired, mismatched, and valid proof behavior.
- Smoke test for the full sensitive-action flow: blocked request, reauth prompt, proof issuance, retry success.

## Assumptions

- The matching `spec.md` will be created or approved before implementation work begins.
- Reauth proof tokens are distinct from access tokens and do not replace existing authorization checks.
- OTP delivery is sent directly from Auth through Resend instead of via the Notification service.
- Existing login, refresh, and normal account flows remain unchanged.
