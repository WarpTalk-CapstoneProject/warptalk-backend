# WarpTalk Backend — Constitution

> **Status**: Immutable governing principles. Amendments require team leader approval + documented rationale.
> **Target**: `warptalk-backend` — .NET 10 Microservices Gateway
> **Last updated**: 2026-04-03

---

## Article I — Clean Architecture (Non-Negotiable)

Every service MUST maintain strict separation across 4 layers:

```
Domain ← Application ← Infrastructure ← API
```

**Rules:**
- `Domain` has NO dependencies on EF Core, HTTP, Redis, gRPC, or any infrastructure library. It only contains entities, value objects, domain events, and interfaces.
- `Application` depends ONLY on `Domain`. It uses interfaces (injected via DI) — never instantiates Infrastructure classes directly.
- `Infrastructure` implements `Domain` interfaces. Contains EF Core DbContexts, Repositories, Redis, gRPC clients.
- `API` orchestrates DI setup, middleware, and delegates to `Application`.

**Project naming convention (per service):**
```
WarpTalk.{Service}Service.Domain
WarpTalk.{Service}Service.Application
WarpTalk.{Service}Service.Infrastructure
WarpTalk.{Service}Service.API
```

**Gate before implementation:**
- [ ] No EF Core / Npgsql references in `.Domain` project
- [ ] No `new` infrastructure class calls inside `Application`
- [ ] No business logic in Controllers or API layer

---

## Article II — Inter-Service Communication

All communication between services MUST use the designated channels below. No exceptions.

### 2.1 Synchronous (.NET ↔ .NET)
**Technology: gRPC only** (`Grpc.AspNetCore v2.76.0`, Protobuf, HTTP/2)

- Gateway → AuthService: token verification, user lookup
- Gateway → MeetingService: meeting state queries
- MeetingService → SubscriptionService: credit deduction
- Any cross-service data that requires an immediate response

> ❌ NEVER use direct HTTP calls (`HttpClient`) between .NET services.
> ❌ NEVER use direct SQL JOINs across schemas.
> ✅ Cross-service foreign keys are resolved **exclusively via gRPC**.

Example cross-service references (always gRPC, no SQL FK):
```
meeting.meetings.host_id      → gRPC → auth.users.id
meeting.meetings.workspace_id → gRPC → auth.workspaces.id
transcript.meeting_id         → gRPC → meeting.meetings.id
notification.user_id          → gRPC → auth.users.id
```

### 2.2 Asynchronous AI Pipeline (.NET → Python Workers)
**Technology: Redis Streams** — fixed key patterns:

| Stream Key | From | To |
|---|---|---|
| `audio:chunks:{meetingId}` | Gateway | STT Worker |
| `stt:results:{meetingId}` | STT Worker | Translation Worker |
| `translate:results:{meetingId}` | Translation Worker | TTS/Voice Clone Worker |
| `tts:results:{meetingId}` | TTS Worker | Gateway → Client |

Audio chunks: **2-second streaming windows** (overlapped). Do NOT batch full utterances.

### 2.3 Asynchronous Event-Driven (.NET ↔ .NET)
**Technology: Redis Streams + Consumer Groups** — for fire-and-forget events:

| Stream Key | Publisher | Consumer |
|---|---|---|
| `events:notification` | Meeting/Subscription | NotificationService |
| `events:subscription` | Meeting | SubscriptionService |

### 2.4 Client Real-Time
**Technology: SignalR** with **Redis Backplane** (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`)

- YARP Sticky Sessions MUST be enabled for SignalR scaling: `SessionAffinity.Policy = Cookie`
- All audio/transcript data over **WSS** (WebSocket Secure). Never plain WS in production.

### 2.5 Caching
**Technology: Redis** (key-value, TTL-based)

| Key Pattern | TTL | Purpose |
|---|---|---|
| `user:session:{userId}` | 15 min | JWT claims + roles |
| `plan:features:{planId}` | 1 hour | Plan feature flags |
| `workspace:settings:{id}` | 10 min | Workspace config |
| `meeting:active:{code}` | No expiry | Active meeting state |
| `rate:limit:{ip}:{path}` | 1 min | Rate limiting counter |

---

## Article III — Database Standards

### 3.1 .NET Version
**Target Framework: `net10.0`**. Never write `.NET 8`, `.NET 9` in code, configs, or comments.

### 3.2 PostgreSQL
- **Schema-per-service**: `auth`, `meeting`, `transcript`, `subscription`, `notification`
- Each service has its own DB user with access ONLY to its schema.
- Connect via **PgBouncer at port 6432** — NEVER connect directly to `postgres:5432`.
- ORM: **EF Core + Npgsql** (`v10.x`). No raw SQL except for complex reporting queries.
- **UUID v7** (time-ordered) as primary keys for all tables.
- All timestamps: `TIMESTAMPTZ` with `DEFAULT NOW()`.

### 3.3 Connection Strings
- **NEVER hardcode** connection strings in source code.
- Always use `Name=` syntax: `optionsBuilder.UseNpgsql(config.GetConnectionString("Default"))`.
- Dev: `.env` file (gitignored). Staging: Docker secrets. Prod: Vault/Key Vault.

### 3.4 Migrations (Zero-Downtime)
**Expand-and-Contract pattern** — NEVER drop or rename a column in a single step:

```
Step 1: Add new column (nullable)         → Deploy
Step 2: Backfill existing data            → Background job
Step 3: Update code to use new column     → Deploy
Step 4: Drop old column                   → Deploy (next sprint)
```

### 3.5 Qdrant (Vector DB)
Three collections for AI workloads:

| Collection | Dimensions | Usage |
|---|---|---|
| `voice_embeddings` | 256–512 | Voice cloning speaker matching |
| `glossary_embeddings` | 768 | Semantic glossary search |
| `transcript_embeddings` | 768 | AI Assistant meeting context |

---

## Article IV — Test-First Development (Non-Negotiable)

**File creation order — NO EXCEPTIONS:**
1. Define contracts (`specs/{feature}/contracts/`)
2. Write **Contract tests** → verify they FAIL (Red)
3. Write **Unit tests** (Domain/Application) → verify they FAIL (Red)
4. Write implementation code → make tests pass (Green)
5. Refactor → tests still pass

**Test types:**
- **Unit tests**: Domain entities, Application handlers. No mocks for business logic.
- **Integration tests**: Use **Testcontainers** (real PostgreSQL + Redis). NO in-memory database.
- **Contract tests**: gRPC proto definitions validated before service implementation.

**Gate before PR:**
- [ ] All tests written before implementation code
- [ ] All tests pass (`dotnet test`)
- [ ] Integration tests use Testcontainers, not mocks
- [ ] Swagger documentation generated

---

## Article V — Branching & Commit Convention

### Branch naming (mandatory prefix):
| Prefix | Usage |
|---|---|
| `feat/` | New feature (must have spec file) |
| `hotfix/` | Production bug fix |
| `refactor/` | Code restructuring (no feature change) |
| `chore/` | Build, deps, config |
| `docs/` | Documentation only |

### Commit format — Conventional Commits:
```
feat(meeting): add lobby participant handling
fix(auth): correct JWT refresh token expiry check
refactor(transcript): extract correction logic to domain service
chore(deps): upgrade Npgsql to 10.0.2
```

### PR rules:
- **Every `feat/` branch MUST have** `.speckit/specs/NNN-{feature}/spec.md` approved before PR is opened.
- PR description MUST link to the spec file.
- No `feat/` PR without passing tests.

---

## Article VI — API Standards

- **URL versioning**: `/api/v1/`. Never use header-based versioning.
- **Swagger**: Mandatory for ALL REST endpoints (`Swashbuckle.AspNetCore`).
- **Error responses**: `ProblemDetails` format (RFC 7807). No raw strings, no custom error objects.
- **Rate limits** (enforced at Gateway):

| Endpoint | Limit | Reason |
|---|---|---|
| `POST /api/v1/auth/login` | 5/min per IP | Brute-force protection |
| `POST /api/v1/auth/register` | 3/hour per IP | Spam prevention |
| `POST /api/v1/meetings` | 10/min per user | Abuse prevention |
| `POST /api/v1/transcript/export` | 5/min per user | CPU-intensive |
| `POST /api/v1/subscription/pay` | 3/min per user | Payment safety |
| WebSocket connection | 1 per user per meeting | Resource protection |

---

## Article VII — Graceful Degradation & SLA

**Performance targets:**
- Translation pipeline end-to-end: **≤ 2 seconds** per semantic chunk
- AI feedback / notifications: **≤ 5 seconds** after transcript segment
- AI meeting summary: **≤ 15 seconds** after meeting ends
- System uptime: **≥ 99%** for core meeting routing

**Fallback hierarchy:**
```
Voice Clone (XTTS v2)
  │ fails / timeout
  ▼
EdgeTTS (standard voice, lower quality)
  │ fails / timeout
  ▼
Text-only mode (transcript displayed, no audio output)
  │ (never disconnect participants)
```

**Gate before implementation:**
- [ ] Every AI-dependent feature has a documented fallback mode
- [ ] Fallback behavior is tested in integration tests

---

## Article VIII — Security (RBAC)

**4 system roles** (in `auth.roles`):
| Role | Scope |
|---|---|
| `system_admin` | Global platform management |
| `workspace_admin` | Workspace-level user/glossary management |
| `host` | Meeting creation, lifecycle management |
| `participant` | Join meetings, select languages, correct transcripts |

**Rules:**
- JWT authentication on ALL endpoints EXCEPT:
  - `POST /api/v1/auth/login`
  - `POST /api/v1/auth/register`
- Account lockout: **5 consecutive failed logins → locked for 5 minutes** (`auth.users.is_locked`, `locked_until`)
- CORS: Only `https://warptalk.vn` and `https://admin.warptalk.vn`
- Required security headers: `X-Content-Type-Options`, `X-Frame-Options`, `X-XSS-Protection`, `Referrer-Policy`

---

## Article IX — Credit System

**Single source of truth**: All credit operations go through `SubscriptionService`. Never deduct credits in a Controller or another service directly.

**Credit formula:**
```
Credits Deducted = BaseRate × UniqueTargetLanguages × DurationHours
```
- **NOT multiplied by number of participants** — only by unique target language streams.
- `BaseRate`: Pro = 30 cr/hr, Premium = 25 cr/hr (voice); 10 cr/hr (text-only)

**Specific rates** (`subscription.usage_records.usage_type`):
| Type | Rate |
|---|---|
| `stt_minutes` | 1.5 credits/min |
| `tts_minutes` | 2 credits/min |
| `translation_chunks` | 1 credit/1000 chars |
| `ai_summary` | 3–5 credits/summary |
| `voice_clone_minutes` | 4 credits/min |

**Warning triggers:**
- Credit ≤ 5 min remaining → SignalR event `CreditLowWarning` to Host
- Credit = 0 → graceful fallback to text-only, no disconnect

---

## Amendment Process

Modifications to this constitution require:
1. Written rationale documented in a `docs/` PR
2. Approval by project Leader (Huỳnh Thái Tú — SE183307)
3. Backwards compatibility assessment
4. Update the "Last updated" date above
