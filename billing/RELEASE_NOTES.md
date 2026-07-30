# WarpTalk Billing Service - Release Notes (Phase 6)

## 1. Overview
The Billing Service manages workspace subscriptions, credit top-ups, and usage consumption. It provides a robust ledger system with idempotency support and multi-tenant authorization.

## 2. Environment Configuration
The service requires the following environment variables or `appsettings.json` configurations:

| Key | Description | Default |
|-----|-------------|---------|
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string | `Host=localhost;Database=warptalk;...` |
| `Jwt:Key` | Key for JWT validation | (From Auth Service) |
| `Jwt:Issuer` | JWT Issuer | `WarpTalk` |
| `Jwt:Audience` | JWT Audience | `WarpTalk.API` |
| `Auth:BypassValidation` | Dev only: Bypass gRPC auth checks | `true` (in Dev) |

## 3. Deployment & Migration
- **Schema**: Billing-owned tables reside in the `subscription` schema. The
  service is intentionally named Billing while preserving the existing
  subscription schema boundary.
- **Migrations**: Manual SQL scripts are located in `src/WarpTalk.BillingService.Infrastructure/Persistence/Migrations/`.
- **Initialization Order**:
    1. `20260506090000_billing_init_schema.sql`
    2. `20260506093000_billing_constraints_indexes_timestamp.sql`
    3. `20260506094500_billing_idempotency_records.sql`

## 4. Rollback & Fallback Notes
- **Rollback**: Each migration script contains a commented-out `DOWN MIGRATION` block. Execute these manually in reverse order if a rollback is needed.
- **Data Integrity**: The `CreditTransactions` table acts as a source of truth ledger. If a credit balance is suspect, it can be recalculated by summing the ledger for a specific `workspace_id`.

## 5. Known Limitations
- **Concurrency**: Credit reservation and ledger writes use PostgreSQL
  transactions plus correlation/idempotency constraints. Subscription
  `xmin` row-version metadata is mapped for optimistic concurrency where the
  aggregate is updated through EF.
- **Auth Integration**: Workspace authorization uses the real
  `WorkspaceService.WorkspaceServiceClient` over internal gRPC with
  fail-closed behavior on dependency errors. Development-only bypasses are
  configuration-gated and are not available in Production.
- **Enums**: Database stores the legacy string values used by the current
  migration set. New code should use the domain constants and migrations
  rather than manual edits.

## 6. Demo Evidence (Functional Endpoints)
- `POST /api/v1/Billing/workspaces/{id}/credits/topup`: Successfully adds credits and returns 200 OK.
- `POST /api/v1/Billing/workspaces/{id}/credits/consume`: Deducts credits, supports idempotency, returns 402 if insufficient.
- `POST /api/v1/Billing/workspaces/{id}/subscriptions/cancel`: Cancels active subscription and returns 200 OK with the final state.
