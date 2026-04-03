# Implementation Plan: {FEATURE_TITLE}

> **Spec**: [NNN-{feature-name}/spec.md](../NNN-{feature-name}/spec.md)
> **Author**: {Name} ({StudentCode})
> **Created**: {YYYY-MM-DD}
> **Status**: `draft` | `approved`

---

## Phase -1: Pre-Implementation Gates

> Complete ALL gates before writing any code. Document any justified exception in section 9.

### Constitution Gate — Article I (Clean Architecture)
- [ ] New code placed in correct layer (`Domain` / `Application` / `Infrastructure` / `API`)
- [ ] No EF Core / Npgsql references added to `.Domain` project
- [ ] No `new` infrastructure class calls inside `Application`
- [ ] No business logic in Controllers

### Constitution Gate — Article II (Communication)
- [ ] Cross-service data fetched via **gRPC** (no SQL FK / HTTP call between services)
- [ ] Async events use **Redis Streams** with correct key pattern (see Constitution §2.2–2.3)
- [ ] New gRPC methods have Protobuf definitions in `WarpTalk.Shared`

### Constitution Gate — Article III (Database)
- [ ] Connects via **PgBouncer** (`port 6432`)
- [ ] No hardcoded connection string in code
- [ ] Migration follows expand-and-contract pattern (if column change)
- [ ] UUID v7 used for new PK columns

### Constitution Gate — Article IV (TDD)
- [ ] Contract tests (gRPC proto) written **BEFORE** implementation
- [ ] Unit tests written and confirmed **FAILING** before implementation
- [ ] Integration tests use **Testcontainers** (no in-memory DB)

### Constitution Gate — Article VI (API)
- [ ] New REST endpoints routed under `/api/v1/`
- [ ] Swagger attributes added
- [ ] Error responses use `ProblemDetails` format

---

## 1. Summary

> High-level technical approach to address the spec. 2–4 sentences. Keep readable.

{Technical summary here}

---

## 2. Affected Services

| Service | Change Type | Description |
|---|---|---|
| `WarpTalk.{Service}.API` | `MODIFY` | {What changes} |
| `WarpTalk.{Service}.Application` | `ADD` | {New class/handler} |
| `WarpTalk.{Service}.Infrastructure` | `MODIFY` | {Repository changes} |
| `WarpTalk.{Service}.Domain` | `ADD` | {New entity / domain event} |

---

## 3. Data Model Changes

> Describe schema changes. List EF Core migrations to be created.
> Follow expand-and-contract if modifying existing columns.

### New Tables / Columns
```sql
-- {ServiceSchema}.{table_name}
ALTER TABLE {schema}.{table} ADD COLUMN {col} {type} NOT NULL DEFAULT {val};
```

### EF Core Migrations Required
- `Add{Entity}Table` — creates `{schema}.{table}`
- `Add{Column}To{Table}` — adds `{col}` column

---

## 4. gRPC Contracts

> Define any new or modified `.proto` methods. These MUST be written before implementation.

```protobuf
// File: WarpTalk.Shared/Protos/{service}.proto
rpc {MethodName} ({RequestMessage}) returns ({ResponseMessage});

message {RequestMessage} {
  string {field} = 1;
}

message {ResponseMessage} {
  bool success = 1;
  string error_message = 2;
}
```

---

## 5. REST Endpoints (if applicable)

| Method | Path | Auth | Request Body | Response |
|---|---|---|---|---|
| `POST` | `/api/v1/{resource}` | JWT | `{RequestDto}` | `{ResponseDto}` |
| `GET` | `/api/v1/{resource}/{id}` | JWT | — | `{ResponseDto}` |

---

## 6. Redis Streams / Events (if applicable)

| Stream Key | Published by | Consumed by | Payload |
|---|---|---|---|
| `events:{topic}` | `{Publisher}` | `{Consumer}` | `{ field: value }` |

---

## 7. New Classes / Files

> List files to be created in implementation order.

```
WarpTalk.{Service}.Domain/
  Entities/         {Entity}.cs
  Events/           {DomainEvent}.cs

WarpTalk.{Service}.Application/
  {Feature}/        {Command/Query}.cs
                    {Command/Query}Handler.cs
                    {Dto}.cs

WarpTalk.{Service}.Infrastructure/
  Repositories/     {Entity}Repository.cs
  Persistence/      (EF Config if needed)

WarpTalk.{Service}.API/
  Controllers/      {Entity}Controller.cs (if REST)
  GrpcServices/     {Service}GrpcServiceImpl.cs (if gRPC)

tests/
  Unit/             {Feature}HandlerTests.cs
  Integration/      {Feature}IntegrationTests.cs
```

---

## 8. Quickstart Validation

> Key scenarios to manually verify after implementation.

1. **Happy path**: {Step-by-step scenario that proves it works}
2. **Fallback / error path**: {What happens when X fails}
3. **RBAC check**: Call endpoint as `participant` role → expect `403 Forbidden`

---

## 9. Complexity Tracking

> If any Constitution Article gate was not fully passed, document the justified exception here.

| Article | Exception | Justification |
|---|---|---|
| — | — | — |

---

## 10. Plan Completeness Checklist

- [ ] All Phase -1 gates checked
- [ ] Data model changes fully described
- [ ] gRPC contracts defined (if applicable)
- [ ] REST endpoints listed (if applicable)
- [ ] No speculative / "might need later" features included
- [ ] All phases have clear deliverables
