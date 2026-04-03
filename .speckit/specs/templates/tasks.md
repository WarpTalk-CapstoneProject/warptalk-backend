# Tasks: {FEATURE_TITLE}

> **Plan**: [NNN-{feature-name}/plan.md](../NNN-{feature-name}/plan.md)
> **Author**: {Name} ({StudentCode})
> **Sprint**: {Sprint number}

> **Task status legend:**
> - `[ ]` Not started
> - `[/]` In progress
> - `[x]` Done
> - `[P]` Can be done in parallel with other `[P]` tasks in same group

---

## Phase 0: Contracts & Test Setup (BEFORE any implementation)

- [ ] Define gRPC proto messages and service methods in `WarpTalk.Shared/Protos/`
- [ ] Define REST request/response DTOs
- [ ] Write contract tests → **verify they FAIL**
- [ ] Write unit tests for Domain entities → **verify they FAIL**
- [ ] Write unit tests for Application handlers → **verify they FAIL**
- [ ] Write integration test stubs (Testcontainers setup) → **verify they FAIL**

> ⛔ DO NOT proceed to Phase 1 until all Phase 0 tests are written and confirmed failing.

---

## Phase 1: Domain Layer

- [ ] Create entity class(es) in `WarpTalk.{Service}.Domain/Entities/`
- [ ] Define domain events in `WarpTalk.{Service}.Domain/Events/`
- [ ] Define repository interface(s) in `WarpTalk.{Service}.Domain/Repositories/`
- [ ] Run domain unit tests → **verify they PASS**

---

## Phase 2: Application Layer

- [ ] [P] Create Command/Query class in `Application/{Feature}/`
- [ ] [P] Create Handler class (implements business logic)
- [ ] [P] Create response DTO(s)
- [ ] Run application unit tests → **verify they PASS**

---

## Phase 3: Infrastructure Layer

- [ ] [P] Create/update repository implementation
- [ ] [P] Create EF Core configuration (if new entity)
- [ ] [P] Create migration: `dotnet ef migrations add {MigrationName} --project Infrastructure --startup-project API`
- [ ] Verify migration SQL looks correct: `dotnet ef migrations script`
- [ ] [P] Implement Redis Stream publisher/consumer (if applicable)
- [ ] [P] Implement gRPC client call (if calling another service)
- [ ] Run integration tests → **verify they PASS**

---

## Phase 4: API Layer

- [ ] [P] Create/update REST Controller (if REST endpoint)
- [ ] [P] Create/update gRPC service implementation (if gRPC server)
- [ ] Add Swagger XML comments to all new endpoints
- [ ] Register new service/handler in DI container (`Program.cs`)
- [ ] Run all tests → **verify everything still PASSES**

---

## Phase 5: Verification

- [ ] `dotnet build` — no warnings, no errors
- [ ] `dotnet test` — all tests pass
- [ ] Swagger UI — new endpoints appear with correct schema
- [ ] Manual happy path test (from Quickstart Validation in plan.md)
- [ ] Manual RBAC test — call with unauthorized role → `403 Forbidden`
- [ ] Manual fallback test (if applicable) — verify graceful degradation

---

## Phase 6: PR Preparation

- [ ] Spec file exists at `.speckit/specs/NNN-{feature}/spec.md` with status `approved`
- [ ] Branch name follows convention: `feat/NNN-{feature-kebab-name}`
- [ ] Commits follow Conventional Commits format
- [ ] PR description links to spec file
- [ ] No debug code or hardcoded values left
- [ ] `dotnet test` passes one final time on clean checkout
