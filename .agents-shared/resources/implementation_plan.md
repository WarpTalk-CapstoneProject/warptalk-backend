# WarpTalk — Implementation Plan (Production-Grade)

> **All 15 audit fixes applied** — speed, scalability, deployment readiness

## Goal

Scaffold the complete microservice project structure for WarpTalk — .NET API Gateway, domain microservices, Python AI workers, infrastructure configs, and client app stubs — with production-grade operational concerns built in from day 1.

## Architecture Overview

```
          ┌──────────────────────────────────┐
          │          Client Layer             │
          │  ┌─────────┐  ┌──────────────┐   │
          │  │ Web App  │  │ Desktop App  │   │
          │  │ (NextJS) │  │ (ElectronJS) │   │
          │  └────┬─────┘  └──┬───────┬───┘   │
          └───────┼───────────┼───────┼───────┘
                  │ HTTPS     │WSS    │WebRTC (audio)
                  ▼           ▼       ▼
          ┌──────────────────────────────────┐
          │       API Gateway (.NET 8)       │
          │  YARP · JWT/RBAC · SignalR Hub   │
          │  Rate Limit · CORS · /api/v1/   │
          │  Health: /health · /ready        │
          └──┬──────┬──────┬──────┬──────┬──┘
             │      │      │      │      │     ← gRPC (sync, Protobuf)
             ▼      ▼      ▼      ▼      ▼
          ┌──────┐┌──────┐┌──────┐┌──────┐┌──────┐
          │ Auth ││Meet- ││Trans-││ Sub- ││Notif.│
          │ Svc  ││ing   ││cript ││scrip.││ Svc  │
          └──┬───┘└──┬───┘└──┬───┘└──┬───┘└──┬───┘
             │       │       │       │       │
             ▼       ▼       ▼       ▼       ▼
          ┌──────────────────────────────────┐
          │          PgBouncer (6432)        │ ← connection pooling
          └──────────────┬───────────────────┘
                         ▼
          ┌──────────────────────────────────┐
          │  PostgreSQL (schema-per-service) │
          │  auth│meeting│transcript│sub│notif│
          └──────────────────────────────────┘
      ┌──────────────────────────────────────────┐
      │  Redis (6379)                            │
      │  Cache (JWT/roles) + Streams (async)     │
      └──────────┬───────────────────────────────┘
           ┌─────┴─────┬──────────┬──────────┐
           ▼           ▼          ▼          ▼
       ┌──────┐  ┌────────┐ ┌──────┐  ┌────────┐
       │ STT  │  │Translat│ │ TTS/ │  │  AI    │  Python Workers
       │Worker│  │ion     │ │Voice │  │Assist. │  (GPU-capable)
       └──────┘  └────────┘ └Clone─┘  └────────┘
       ▲ streaming pipeline: 2s chunks, overlapped ▲
```

---

## Inter-Service Communication

| Type | Tech | Example |
|---|---|---|
| **Sync** | gRPC (Protobuf, HTTP/2) | Gateway → Auth: verify token |
| **Async** | Redis Streams + Consumer Groups | Meeting → Notification: send email |
| **AI Pipeline** | Redis Streams (streaming, 2s chunks) | Audio → STT → Translate → TTS |
| **Client ↔ GW** | SignalR + WebRTC | Desktop ↔ Gateway real-time audio |
| **Cache** | Redis (key-value, TTL) | JWT claims, plan features, rate limits |

---

## Folder Structure

> Each top-level folder = **independent GitHub repo**

```
WarpTalk - Capstone Project/
├── warptalk-backend/                  ← .NET 8 Gateway + Services
│   ├── WarpTalk.Gateway/              # YARP + SignalR + CORS + Rate Limit
│   ├── WarpTalk.AuthService/
│   ├── WarpTalk.MeetingService/
│   ├── WarpTalk.TranscriptService/    # + Glossary (Qdrant)
│   ├── WarpTalk.SubscriptionService/
│   ├── WarpTalk.NotificationService/
│   ├── WarpTalk.Shared/               # DTOs, Protobuf, Result types
│   ├── WarpTalk.Tests/
│   ├── WarpTalk.sln
│   └── docker-compose.yml
│
├── warptalk-ai/                       ← Python AI Workers
│   ├── shared/                        # Redis client, audio utils, Protobuf
│   ├── stt-worker/
│   ├── translation-worker/
│   ├── tts-worker/
│   ├── ai-assistant-worker/
│   ├── tests/
│   └── pyproject.toml
│
├── warptalk-web/                      ← NextJS Portal
├── warptalk-desktop/                  ← ElectronJS + Virtual Audio Drivers
│
└── warptalk-infrastructure/           ← DevOps & Orchestration
    ├── docker-compose.yml             # Full stack
    ├── docker-compose.dev.yml         # Dev overrides
    ├── docker-compose.prod.yml        # Production scaling
    ├── pgbouncer/
    │   └── pgbouncer.ini
    ├── coturn/
    │   └── turnserver.conf            # Primary + backup TURN/STUN
    ├── observability/
    │   ├── otel-collector.yml
    │   ├── prometheus.yml
    │   ├── seq/
    │   └── dashboards/                # Grafana JSON
    ├── backup/
    │   ├── pg-backup.sh               # Daily pg_dump → S3/MinIO
    │   └── qdrant-backup.sh
    └── scripts/
        ├── start-all.sh
        ├── stop-all.sh
        ├── init-db.sh
        └── seed-data.sh
```

---

## Key Design Decisions

### Database: Schema-per-Service

1 PostgreSQL container → 5 schemas, each with isolated DB user:

```sql
CREATE USER auth_svc     WITH PASSWORD '...';
CREATE USER meeting_svc  WITH PASSWORD '...';
CREATE USER transcript_svc WITH PASSWORD '...';
CREATE USER sub_svc      WITH PASSWORD '...';
CREATE USER notif_svc    WITH PASSWORD '...';

GRANT USAGE ON SCHEMA auth TO auth_svc;
GRANT ALL ON ALL TABLES IN SCHEMA auth TO auth_svc;
-- repeat for each service
```

Full schema: [database_schema.md](file:///Users/danchoingoinhinmuaroi/.gemini/antigravity/brain/4871e63e-aff9-44c9-88c4-a9654aca1042/database_schema.md) (33 tables, 60+ indexes, 4 partitioned)

### Zero-Downtime Migrations (EF Core)

```
Rule: NEVER drop/rename columns in 1 step. Use expand-and-contract:
  Step 1: Add new column (nullable)       → Deploy
  Step 2: Backfill data                   → Background job
  Step 3: Update code to use new column   → Deploy
  Step 4: Drop old column                 → Deploy (next sprint)
```

---

## Infrastructure Components

### PgBouncer — Connection Pooling

```yaml
pgbouncer:
  image: edoburu/pgbouncer:latest
  environment:
    DATABASE_URL: postgres://warptalk:${DB_PASSWORD}@postgres:5432/warptalk
    POOL_MODE: transaction
    MAX_CLIENT_CONN: 200
    DEFAULT_POOL_SIZE: 20
  ports: ["6432:6432"]
  depends_on: [postgres]
```

All services connect to `pgbouncer:6432` NOT `postgres:5432`.

### Redis — Cache + Streams

```
Cache Keys (speed optimization):
├── user:session:{userId}     TTL=15min   # JWT claims + roles
├── plan:features:{planId}    TTL=1h      # Plan features
├── workspace:settings:{id}   TTL=10min   # Workspace config
├── meeting:active:{code}     TTL=0       # Active meeting (no expiry)
└── rate:limit:{ip}:{path}    TTL=1min    # Rate limiting

Stream Keys (async events):
├── audio:chunks:{meetingId}              # Raw audio → STT
├── stt:results:{meetingId}               # STT → Translator
├── translate:results:{meetingId}         # Translator → TTS
├── tts:results:{meetingId}               # TTS → Client
├── events:notification                   # → NotificationService
└── events:subscription                   # → SubscriptionService
```

### TURN/STUN — High Availability

```yaml
coturn-primary:
  image: coturn/coturn:latest
  ports: ["3478:3478/udp", "3478:3478/tcp"]
  volumes: ["./coturn/turnserver.conf:/etc/coturn/turnserver.conf"]

coturn-backup:
  image: coturn/coturn:latest
  ports: ["3479:3478/udp", "3479:3478/tcp"]
  volumes: ["./coturn/turnserver.conf:/etc/coturn/turnserver.conf"]
```

Gateway provides both endpoints to clients; client picks fastest via ICE.

---

## AI Streaming Pipeline

```
BEFORE (batch):  Audio 10s → STT 3s → Translate 2s → TTS 3s = 8s latency

AFTER (streaming, 2s chunks):
  Chunk 1 → STT 0.5s → Translate 0.4s → TTS 0.6s = 1.5s first output
  Chunk 2 → overlapped with Chunk 1 processing
  Chunk 3 → ...
```

### GPU Resource Isolation

```yaml
stt-worker:
  deploy:
    resources:
      reservations:
        devices: [{ driver: nvidia, count: 1, capabilities: [gpu] }]
      limits: { memory: 4G }
  environment:
    CUDA_VISIBLE_DEVICES: "0"

tts-worker:
  deploy:
    resources:
      limits: { memory: 8G }
  environment:
    CUDA_VISIBLE_DEVICES: "1"
```

---

## Security

### CORS + Headers (Gateway)

```csharp
app.UseCors(p => p
    .WithOrigins("https://warptalk.vn", "https://admin.warptalk.vn")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials());

app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});
```

### Rate Limiting

| Endpoint | Limit | Strategy |
|---|---|---|
| `/api/v1/auth/login` | 5/min per IP | Brute force protection |
| `/api/v1/auth/register` | 3/hour per IP | Spam prevention |
| `/api/v1/meeting/create` | 10/min per user | Abuse prevention |
| `/api/v1/transcript/export` | 5/min per user | CPU-intensive |
| `/api/v1/subscription/pay` | 3/min per user | Payment safety |
| WebSocket | 1 conn/user/meeting | Resource protection |
| AI Pipeline | By subscription credits | Business logic |

### Secret Management

```
DEV:     .env files (gitignored)
STAGING: Docker secrets
PROD:    Azure Key Vault / HashiCorp Vault

Secrets: DB_PASSWORD, REDIS_PASSWORD, JWT_SECRET_KEY, PAYOS_API_KEY,
         PAYOS_CHECKSUMKEY, SMTP_PASSWORD, QDRANT_API_KEY,
         GOOGLE_OAUTH_SECRET, AI_MODEL_API_KEYS
```

---

## Observability

### Health Checks (every service)

```csharp
app.MapHealthChecks("/health");       // Liveness
app.MapHealthChecks("/ready", new()   // Readiness
{
    Predicate = check => check.Tags.Contains("ready")
});

services.AddHealthChecks()
    .AddNpgSql(connStr, tags: new[] { "ready" })
    .AddRedis(redisStr, tags: new[] { "ready" });
```

### Logging Pipeline

```
Service → Serilog (structured JSON) → OpenTelemetry Collector → Seq
Service → OTel Metrics → Prometheus → Grafana
```

### Key Dashboards

| Dashboard | Metrics |
|---|---|
| API Gateway | RPS, latency p50/p95/p99, error rate |
| Meetings | Active meetings, concurrent participants |
| AI Pipeline | Processing latency, queue depth, GPU utilization |
| Subscription | Credit consumption rate, payment success rate |
| Infrastructure | CPU, RAM, disk, network per container |

---

## Scaling Strategy

### Horizontal (Docker Compose / Swarm / K8s)

```yaml
# docker-compose.prod.yml
services:
  gateway:             { deploy: { replicas: 2 } }    # Load-balanced
  auth-service:        { deploy: { replicas: 2 } }    # Stateless
  meeting-service:     { deploy: { replicas: 3 } }    # Highest load
  transcript-service:  { deploy: { replicas: 2 } }
  subscription-service:{ deploy: { replicas: 1 } }    # Low traffic
  notification-service:{ deploy: { replicas: 2 } }
```

YARP sticky sessions for SignalR:
```json
{ "SessionAffinity": { "Enabled": true, "Policy": "Cookie" } }
```

### Auto-Scaling Triggers (future)

| Service | Trigger | Action |
|---|---|---|
| Gateway | CPU > 70% for 2min | +1 replica (max 4) |
| Meeting | Active connections > 500 | +1 replica (max 5) |
| STT Worker | Stream lag > 100 msgs | +1 worker |
| TTS Worker | Stream lag > 50 msgs | +1 worker |
| PostgreSQL | Connections > 80% | Alert → scale vertically |

### Read Replicas (future, production)

```
WRITES → Primary PostgreSQL
READS  → Async replica (~10ms lag) for FTS, audit, history
```

---

## Backup & Recovery

| System | Strategy | Retention |
|---|---|---|
| PostgreSQL | Daily `pg_dump` → S3/MinIO + hourly WAL archiving | 30 days |
| Redis | RDB every 15min + AOF persistence | 7 days |
| Qdrant | Daily snapshot API → S3 | 30 days |
| Monthly | Test restore procedure | — |

---

## Verification Plan

### Automated

1. `dotnet build WarpTalk.sln` — all .NET projects compile
2. `dotnet test` — unit + integration tests pass
3. `pip install -e .` in `warptalk-ai/` — dependencies resolve
4. `pytest` — AI worker tests pass
5. `docker compose config` — validates YAML
6. `curl /health` — all services respond 200
7. `curl /ready` — all readiness checks pass

### Manual

- Each repo: `.agents/`, `.gitignore`, `README.md`, `.github/workflows/`
- Each service: own `Dockerfile`, own `appsettings.json`
- Gateway: Swagger UI, CORS headers, security headers verified
- TURN/STUN: ICE connectivity test with both endpoints
- `docker compose -f docker-compose.prod.yml config` validates scaling
