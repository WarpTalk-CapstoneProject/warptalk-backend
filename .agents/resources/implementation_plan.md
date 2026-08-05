# WarpTalk — Implementation Plan (Production-Grade)

> **All 15 audit fixes applied** — speed, scalability, deployment readiness

## Goal

Scaffold the complete microservice project structure for WarpTalk — .NET API Gateway, domain microservices, Python AI workers, infrastructure configs, and client app stubs — with production-grade operational concerns built in from day 1.

## Architecture Overview

The WarpTalk system is designed as a distributed, high-performance, real-time communication platform utilizing a Microservices Architecture at the backend, a modern Frontend Layer (Web + Desktop), and dedicated External/Third-Party integrations.

```
+-----------------------------------------------------------------------------------+
|                                  FRONTEND LAYER                                   |
|  +--------------------------------------------------+  +-----------------------+  |
|  |         Next.js 16 & React 19 Web Portal         |  | Electron.js App (WAD) |  |
|  |  +--------------------------------------------+  |  | (Virtual Audio)       |  |
|  |  | Edge Middleware (Routing & OAuth2/Auth)    |  |  |                       |  |
|  |  +---------------------v----------------------+  |  |                       |  |
|  |  |     App Router Pages & Feature Components   |  |  |                       |  |
|  |  +---------------------v----------------------+  |  |                       |  |
|  |  |    Zustand Stores & HTTP Services (Axios)  |  |  |                       |  |
|  |  +---------------------v----------------------+  |  |                       |  |
|  |  |  Real-time: LiveKit WebRTC & SignalR Client|  |  |                       |  |
|  |  +--------------------------------------------+  |  |                       |  |
|  +--------------------------------------------------+  +-----------------------+  |
+------------------|------------------------------------------|---------------------+
                   | HTTPS / WSS [Bearer JWT]                 | WebRTC (Audio/Video)
                   v                                          v
+-------------------------------------------------------------|---------------------+
|                                 EDGE GATEWAY LAYER          |                     |
|  +-------------------------------------------------------+  |                     |
|  |                API Gateway (YARP & .NET 10)           |  |                     |
|  |     CORS - Rate Limiting - JWT/RBAC Auth - SignalR     |  |                     |
|  +-------------------------------------------------------+  |                     |
+--------------------------|----------------------------------|---------------------+
                           | gRPC [X-Internal-Context]        |                     |
                           v                                  v
+-------------------------------------------------------------|---------------------+
|                                 BACKEND LAYER (DOCKERIZED)  |                     |
|       |        |        |        |        |        |        |                     |
|       | gRPC   | gRPC   | gRPC   | gRPC   | gRPC   | gRPC   |                     |
|       v        v        v        v        v        v        |                     |
|  <═════════════ Inter-Service gRPC & Events ════════════>    |                     |
|  +--------+ +--------+ +--------+ +--------+ +--------+ +--------+                 |
|  |  Auth  | |Worksp. | |Trans-  | |Trans-  | |Meeting | |Notif-  |                 |
|  | Service| | Service| |transcriptSvc| |RoomSvc | | Service| |ication |                 |
|  +----+---+ +----+---+ +----+---+ +----+---+ +----+---+ +----+---+                 |
|       |          |          |          |          |          |                    |
|       +----------+----------+----+-----+----------+----------+                    |
|                                  | (Npgsql Engine)                                |
|                                  v                                                |
|                          +---------------+                                        |
|                          |   PgBouncer   | (Transaction Pooling)                  |
|                          +-------+-------+                                        |
|                                  v                                                |
|  +-------------------------------+-----------------------+                        |
|  |  PostgreSQL Database (Multi-schema Engine)            |                        |
|  |  auth | workspace | translation_room | transcript ...  |                        |
|  +-------------------------------------------------------+                        |
|  |  MinIO / Local Storage (Binary Document Store)         |                        |
|  +-------------------------------------------------------+                        |
|  |  Qdrant Vector Database (Vector Store & Semantic RAG)  |                        |
|  |  Port: 6333 (REST) / 6334 (gRPC)                      |                        |
|  +-------------------------------------------------------+                        |
|  |  Redis 7 (Cache, Sessions & Backplane)                |                        |
|  +-------------------------------------------------------+                        |
|  |  RabbitMQ (Durable Exchanges & Event Queues)          |                        |
|  +----+--------------------------------------------------+                        |
|       |                                                                           |
|       v (Events)                                                                  |
|  +--------+  +--------+  +--------+  +--------+                               |
|  | STT    |  | Trans- |  | TTS    |  | AI     |                               |
|  | Whisper|  | lation |  | Worker |  | Assist.|  Python GPU AI Workers        |
|  | (CUDA) |  | (NLLB) |  | (XTTS) |  | (GPT4o)|  (PyTorch / CUDA Inference)   |
|  +--------+  +--------+  +--------+  +--------+                               |
+-----------------------------------------------------------------------------------+
                                                              |
+-------------------------------------------------------------|---------------------+
|                            THIRD-PARTY / EXTERNAL SERVICES  |                     |
|  +-----------------------+              +-------------------|---+                 |
|  | Microsoft Presidio    |              | LiveKit Cloud     |<--+                 |
|  | (PII / DLP Scanner)   |              | (WebRTC SFU)      |                     |
|  +-----------------------+              +-----------------------+                 |
|  +-----------------------+              +-----------------------+                 |
|  | SMTP Email Gateway    |              | External LLMs/AIs     |                 |
|  | (SES / SendGrid)      |              | (Gemini, OpenAI...)   |                 |
|  +-----------------------+              +-----------------------+                 |
|  +-----------------------+              +-----------------------+                 |
|  | Google OAuth 2.0 (IdP)|              | Stripe                |                 |
|  |                       |              | (Payment Gateway)     |                 |
|  +-----------------------+              +-----------------------+                 |
|  +-----------------------+                                                        |
|  | AWS S3 (Cloud Storage)|                                                        |
|  +-----------------------+                                                        |
+-----------------------------------------------------------------------------------+
```

### 1. Frontend Layer
*   **Next.js 16 Web Portal (React 19):** Responsive client dashboard, workspace switcher, and meeting rooms interface. Built on **React 19** and **Next.js 16** (App Router) with Tailwind CSS. It leverages key third-party libraries and integrations:
    *   **Real-time Media & WebRTC:** `livekit-client` and `@livekit/components-react` to join, subscribe to, and render real-time WebRTC audio/video feeds.
    *   **Real-time State Synchronization:** `@microsoft/signalr` to communicate with the Gateway Hub for instant state change notifications.
    *   **Data Fetching & Caching:** `axios` paired with `@tanstack/react-query` (TanStack Query) for declarative data fetching, loading states, and client caching.
    *   **State Management:** `zustand` for lightweight, predictable client-side global store.
    *   **Auth & Third-Party Helpers:** `next-auth` for OAuth/credential session management (Client-side flow orchestration, Google redirection, and local session caching), and `@supabase/supabase-js` for secondary real-time backend/database interactions.
    *   **Rich Text Editor:** `@tiptap/react` (Tiptap) for interactive chat and mention overlays.
    *   **Animations:** `gsap` (GreenSock) and `motion` (Framer Motion) for premium visual micro-interactions and transitions.
*   **Electron.js Desktop Client:** Tailored desktop application capable of low-level system audio capture using custom Virtual Audio Drivers (WAD), streaming WebRTC audio directly to the processing pipelines.

### 2. Backend Layer (Microservices & Infrastructure)
The WarpTalk backend utilizes a **Microservices Architecture** running on **.NET 10** alongside dedicated **Python GPU AI workers**. 

#### API Gateway
*   **Technology:** ASP.NET Core & **YARP (Yet Another Reverse Proxy)**.
*   **Role:** Single entry-point for clients. Manages request routing, SSL termination, CORS policies, rate limiting, centralized JWT validation, and houses the SignalR Hub backplane for real-time client state synchronization.

#### Domain Microservices
Each service represents a logical boundary, packaging its own business domain:
*   **Auth Service:** Identity provider managing user profiles, role-based access control (RBAC), and Google OAuth integration (Backend validation, Google ID mapping in DB, and access/refresh token generation). Secure password storage is enforced using **BCrypt** hashing with unique salts.
*   **Workspace Service:** Manages multi-tenant Enterprise Workspaces, domain-verification flows, invitations, document library metadata, and access policies.
*   **TranslationRoom Service:** Manages translation sessions, real-time participant statuses, dynamic audio routing graphs, and triggers finalization of meeting transcripts, summaries, and recording artifacts.
*   **Meeting Service:** Integrates with the WebRTC coordinator to bootstrap video/audio configurations and handle active room credentials.
*   **Transcript Service:** Real-time speech-to-text storage, transcript translation sync, historical search, and corrections.
*   **Notification Service:** Dispatches asynchronous notification payloads (email and push messages) with retry policies.
*   **Billing Service:** Handles enterprise billing subscriptions, billing plans, Stripe checkout, webhooks, and subscription-based feature limits.

#### Clean Architecture Layering (4-Layer Pattern)

Each .NET microservice is structured into four isolated layers to decouple core domain logic from external technical details. All source code dependencies flow inwards toward the Domain core:

```mermaid
graph TD
    %% Define Nodes
    API["API Layer (WarpTalk.*.API)<br/>[Controllers, gRPC Endpoints, Middlewares, Program.cs]"]
    Infra["Infrastructure Layer (WarpTalk.*.Infrastructure)<br/>[DbContext, Npgsql, Redis, RabbitMQ, Repositories]"]
    App["Application Layer (WarpTalk.*.Application)<br/>[CQRS Handlers, Validators, DTOs, Interfaces]"]
    Domain["Domain Layer (WarpTalk.*.Domain)<br/>[Entities, Aggregate Roots, Value Objects, Enums]"]

    %% Define Dependencies (Inward Flow)
    API -->|Depends on| App
    API -->|Bootstraps DI / References| Infra
    Infra -->|Implements Interfaces / Depends on| App
    Infra -->|Depends on| Domain
    App -->|Depends on| Domain

    %% Styling
    style Domain fill:#c2e7ff,stroke:#005c97,stroke-width:2px;
    style App fill:#d3e3fd,stroke:#0b57d0,stroke-width:2px;
    style Infra fill:#f1f3f4,stroke:#5f6368,stroke-dasharray: 5 5;
    style API fill:#e8f0fe,stroke:#1a73e8,stroke-width:2px;
```

```
+-----------------------------------------------------------------------------------+
|                            SERVICE CLEAN ARCHITECTURE                             |
|                                                                                   |
|  +-----------------------------------------------------------------------------+  |
|  |                            API / PRESENTATION LAYER                         |  |
|  |  * Controllers (REST)     * gRPC Endpoint Services  * Middlewares           |  |
|  |  * Program.cs Bootstrapper * Custom HTTP Transforms (X-Internal-Context)    |  |
|  +----+----------------------------------------+-------------------------------+  |
|       |                                        |                                  |
|       | Calls CQRS / Services                  | Resolves DI / Bootstraps         |
|       v                                        v                                  |
|  +----+----------------------------------------+-------------------------------+  |
|  |                                APPLICATION LAYER                            |  |
|  |  * CQRS Handlers (MediatR) * Business Services      * FluentValidators      |  |
|  |  * DTO Definitions         * Static Mapper Classes  * Application Exceptions|  |
|  +----+------------------------------------------------------------------------+  |
|       |                                                                           |
|       | Orchestrates & maps Domain                                                |
|       v                                                                           |
|  +----+------------------------------------------------------------------------+  |
|  |                                  DOMAIN LAYER                               |  |
|  |  * Core Entities           * Aggregate Roots        * Value Objects         |  |
|  |  * Domain-specific Enums   * Domain Exceptions      * Custom Domain Rules   |  |
|  +----+------------------------------------------------------------------------+  |
|       ^                                                                           |
|       | Implements repository contracts & references Domain model                 |
|       |                                                                           |
|  +----+------------------------------------------------------------------------+  |
|  |                              INFRASTRUCTURE LAYER                           |  |
|  |  * EF Core DbContext       * Npgsql SQL Engine      * Repository Implement. |  |
|  |  * Redis Cache Wrapper     * RabbitMQ Event Pub.    * gRPC Client Wrappers  |  |
|  +-----------------------------------------------------------------------------+  |
|                                                                                   |
+-----------------------------------------------------------------------------------+
```

1.  **Domain Layer (`WarpTalk.*Service.Domain`):** Core enterprise business rules. Contains domain entities, aggregate roots, specific enums, value objects, and repository contract interfaces. It is pure C# and has **zero dependencies** on databases, ORMs, or frameworks.
2.  **Application Layer (`WarpTalk.*Service.Application`):** Coordinates application use cases. Implements the CQRS pattern (using MediatR command and query handlers), FluentValidation rules, and business orchestration.
    *   **DTOs (Data Transfer Objects):** Located in the `DTOs/` folder. They represent technology-agnostic data contracts returned or accepted by CQRS commands/queries.
    *   **Mappers (Static Extension Classes):** Located in the `Mappers/` folder. High-performance, fully type-safe, compile-time manual mapping static classes (e.g., `WorkspaceMapper` using extension methods like `ToDto()` and `ToEntity()`) to map Domain Entities directly to Application DTOs. This avoids the reflection and runtime overhead associated with libraries like AutoMapper or Mapster.
3.  **Infrastructure Layer (`WarpTalk.*Service.Infrastructure`):** Implements all technical details. Contains EF Core `DbContext` configurations, repositories mapping to databases, **Npgsql database operations**, Redis caching clients, RabbitMQ event publishers, and external HTTP/gRPC API client wrappers. It depends on both Domain and Application layers.
4.  **API Layer (`WarpTalk.*Service.API`):** Presentation boundary. Contains REST controllers (which serialize DTOs to JSON), gRPC endpoint implementations (which map DTOs to Protobuf contracts), custom HTTP middlewares (e.g., verifying signed workspace contexts), and service startup pipelines (`Program.cs`). It depends on Application and Infrastructure layers.

#### Security & Tenant Identity Propagation (JWT + Signed Context)
To prevent context spoofing and enforce strict multi-tenancy boundaries across downstream services, WarpTalk implements a cryptographic token validation mechanism:
*   **Client-to-Gateway (Standard JWT):** The client authenticates against the YARP API Gateway using a standard Bearer JWT containing the user's primary identity claims.
*   **Service-to-Service (Signed Internal Context - `X-Internal-Context`):** 
    *   Once the Gateway validates the client's JWT and resolves their active tenant workspace, it generates a custom, tamper-resistant JWT signed with an internal **infrastructure shared secret**.
    *   This token is injected into downstream requests via the `X-Internal-Context` header.
    *   It securely propagates critical tenant claims: `sub` (User ID), `workspace_id` (Active Tenant ID), `role` (Workspace Role), and `membership_type` (Internal/External).
    *   All downstream microservices intercept incoming calls using `InternalContextMiddleware`, which cryptographically validates the token signature, checks user blacklist/revocation status via `ITokenBlacklistService`, and binds the validated context (`IWorkspaceContext`) to the request lifetime before executing any downstream logic.
*   **Detailed Technical Specification:** For full architectural implementation, including asynchronous context propagation via RabbitMQ/Redis Streams and C# DbContext Global Query Filters, see [multi_tenancy_security_spec.md](file:///c:/Users/Admin/Documents/WarpTalk%20-%20Capstone%20Project/.agents/resources/multi_tenancy_security_spec.md).

#### Storage, Caching, and Message Broker
*   **Database Partitioning (Multi-Schema PostgreSQL):** All microservices connect to a single PostgreSQL physical database instance using a logical **Database-per-Service (Schema-per-Service)** model (e.g., schemas `auth`, `workspace`, `translation_room`, `transcript`). Cross-schema joins are strictly forbidden; data resolution must occur synchronously via gRPC clients or asynchronously via message events.
*   **Binary Document Storage (S3 / MinIO / Local):** Handles binary file and workspace document storage. The encryption mechanisms depend on the configured provider:
    *   **AWS S3 / MinIO:** Leverage cloud-native Server-Side Encryption (SSE) on the storage layer.
    *   **Local Storage:** Employs application-layer **Encrypt-then-MAC** utilizing **AES-256-CBC** for encryption and **HMAC-SHA512** for authenticity verification, with keys derived dynamically from the active workspace key to prevent local file leakages.
*   **Connection Pooling (PgBouncer):** Situated between .NET services and the PostgreSQL engine to pool connections in `transaction` mode.
*   **Vector Database (Qdrant):** Co-located vector storage running on ports `6333` (HTTP) and `6334` (gRPC). Used for Semantic Search (RAG) and glossary term extraction in the translation pipeline. PostgreSQL remains the relational SQL source of truth, storing metadata references to Qdrant IDs (`qdrant_point_id`).
*   **Distributed Cache (Redis 7):** Handles short-lived token claims, active workspace selections, session caching, and internal state backplane.
*   **Durable Messaging (RabbitMQ):** Facilitates resilient event-driven architecture, pushing heavy background tasks (like document scanning and OCR) through structured queues, DLQs (Dead-letter Queues), and automatic retries.
*   **Python AI Workers:** Dedicated, Dockerized Python (3.11+) workers consuming events from RabbitMQ and Redis Streams to execute GPU-bound AI inference tasks. Key workers include:
    *   **STT Worker (Speech-to-Text):** Powered by `faster-whisper` (utilizing CUDA-accelerated Whisper models with INT8 quantization and VAD filters for sub-200ms processing of 1-second chunks).
    *   **Translation Worker:** Uses Hugging Face `transformers` running Facebook's `nllb-200-distilled-600M` model on GPU with a fallback to the Google Translate API via the `deep-translator` library.
    *   **TTS Worker (Text-to-Speech / Voice Cloning):** Built with `TTS` (Coqui XTTS v2 for dynamic voice cloning based on short audio embeddings) and `edge-tts` (Microsoft Edge TTS API for fallback and default system voices).
    *   **AI Assistant Worker:** Leverages the `openai` async Python client to interact with OpenAI's GPT-4o model for generating structured meeting summaries and checkbox action items.

### 3. Third-Party & External Services
*   **LiveKit (WebRTC SFU):** The real-time media engine routing WebRTC audio and video streams between room participants.
*   **COTURN (STUN/TURN):** Acts as the NAT traversal server to establish peer connections behind enterprise firewalls.
*   **Microsoft Presidio API:** Used within the document ingestion pipeline to identify and mask PII (Personally Identifiable Information) before indexing files.
*   **External LLMs (Gemini/OpenAI/Anthropic):** APIs used by AI workers to translate text, extract keywords, and generate summaries.
*   **SMTP Gateway (SES/SendGrid):** External mail relay used by the Notification Service to deliver validation tokens and team invitations.
*   **Stripe (Payment Gateway):** Secure payment processor integrated with the Billing Service to handle platform subscriptions, invoice generation, checkout sessions, and webhook processing.

---

## Inter-Service Communication

| Type | Tech | Example / Use Case |
|---|---|---|
| **Synchronous** | gRPC (Protobuf, HTTP/2) | TranslationRoom Service validating room creation against Workspace Service policies. |
| **Asynchronous** | RabbitMQ (Exchanges, Queues, DLQ) | Workspace Service publishing `DocumentUploaded` to trigger worker OCR and indexing. |
| **Real-time State** | SignalR (WebSockets) | Broadcasting audio status updates down to the client layout. |
| **Cache & Backplane** | Redis (Distributed Cache) | Synchronizing active workspace tokens and rate limits. |
| **Media Stream** | WebRTC (LiveKit API) | Delivering low-latency audio/video packets between client and SFU. |

---

## Frontend Layer (Client Application)

The WarpTalk web portal is designed as a highly responsive, modern client built on **React 19** and **Next.js 16** (App Router). It features a multi-tiered architecture that handles edge routing security, state synchronization, dynamic animations, and WebRTC media streaming.

```
+-----------------------------------------------------------------------------------+
|                            FRONTEND LAYER ARCHITECTURE                            |
|                                                                                   |
|  +-----------------------------------------------------------------------------+  |
|  |                            EDGE MIDDLEWARE LAYER                            |  |
|  |  * Role-based Route Guarding    * activeWorkspace Verification Check        |  |
|  |  * Session & OAuth2 (Next-Auth)  * Next.js Edge Routing redirects            |  |
|  +----+------------------------------------------------------------------------+  |
|       |                                                                           |
|       | Authorizes & Redirects                                                    |
|       v                                                                           |
|  +----+------------------------------------------------------------------------+  |
|  |                            APP ROUTER (ROUTING / VIEWS)                     |  |
|  |  * Public Routes (Login, Join)   * Admin Console Routes (/admin)            |  |
|  |  * Workspace Admin (/workspace)  * Meeting Rooms & AI Features (/(app))      |  |
|  +----+------------------------------------------------------------------------+  |
|       |                                                                           |
|       | Composes layout views                                                     |
|       v                                                                           |
|  +----+----------------------------------------+-------------------------------+  |
|  |                  COMPONENTS LAYER           |         FEATURES LAYER        |  |
|  |  * UI Primitives (Shadcn/BaseUI)            |  * Live Meeting (WebRTC)      |  |
|  |  * Shared Layouts (Sidebar, HostSidebar)    |  * Transcript & Translation   |  |
|  |  * Common Modals & Forms                    |  * Glossary & AI Assistant    |  |
|  +----+----------------------------------------+----------------+--------------+  |
|       |                                                         |                 |
|       | Triggers actions / binds state                          | Consumes states |
|       v                                                         v                 |
|  +----+---------------------------------------------------------+--------------+  |
|  |                         GLOBAL STATE STORES (ZUSTAND)                      |  |
|  |  * useAuthStore   * useWorkspaceStore   * useMeetingStore   * useSignalRStore  |  |
|  +----+------------------------------------------------------------------------+  |
|       |                                                                           |
|       | Communicates with server-side API Gateway                                 |
|       v                                                                           |
|  +----+----------------------------------------+-------------------------------+  |
|  |                  API SERVICES (AXIOS)       |     SIGNALR & WEBRTC (LIVEKIT) |  |
|  |  * authService   * workspaceService         |  * useSignalR (State Sync WSS)|  |
|  |  * TanStack React Query Hooks              |  * LiveKit WebRTC client      |  |
|  +---------------------------------------------+-------------------------------+  |
|                                                                                   |
+-----------------------------------------------------------------------------------+
```

### 1. Edge Middleware & Route Guarding (`src/middleware.ts`)
*   **Routing Security:** Validates session tokens and routes traffic dynamically based on user credentials.
*   **Tenant/Workspace Tenancy Check:** Intercepts incoming client URLs to verify active workspace parameters (`workspaceSlug`) and redirects unauthenticated or suspended users to their respective home spaces.
*   **Role Enforcement:** Separates routing logic between Owner, Admin, Member, and External Guest users at the Edge, rejecting unauthorized access before reaching Next.js server-side rendering pipelines.

### 2. Next.js App Router (`src/app`)
*   **Admin Console `/admin`:** Secure dashboard for platform operators to monitor tenants, vector db health, local worker telemetry, and platform-wide configurations.
*   **Workspace Portal `/workspace`:** Core space for workspace managers to customize security policies, upload/audit document libraries, configure AI glossary tone/styles, and manage tenant billing.
*   **Meeting surface `/(app)/[workspaceSlug]`:** The main user interface. It hosts the active workspace switcher, meeting creation panel, Live Meeting layout, and interactive AI transcript/document search screens.
*   **Public Portal `/join`:** Minimalistic and optimized entry point for guest users joining a translation room using a code, bypassing full login procedures.

### 3. Modular Feature Boundaries (`src/features` & `src/components/features`)
To maintain high co-location cohesion, code is grouped by business feature rather than technical type:
*   **Workspaces Feature:** Encapsulates creation dialogs, invitation management, verified domain checkers, and dashboard statistics cards.
*   **Meeting Room & Chat Feature:** Orchestrates LiveKit video layouts, speaker flags, real-time translated subtitles overlays, and live chat widgets.
*   **Glossary & Ingestion Feature:** Renders taxonomy management forms, document drop zones, status progress trackers, and audit history.

### 4. Global State Stores (`src/stores` - Zustand)
*   **`useAuthStore`:** Manages currently active JWT tokens, token expiration timers, and local user profile metadata cache.
*   **`useWorkspaceStore`:** Tracks the currently selected Enterprise Workspace, available lists of workspaces, active verified domains, and policies.
*   **`useMeetingStore`:** Manages the active translation room session, current participant rosters, audio routes, and connection metrics.

### 5. Services & Real-time Adapters (`src/services` & `src/lib`)
*   **Axios HTTP Clients:** Maps backend endpoints into reusable async service wrappers, integrated with `@tanstack/react-query` to provide automatic retries, optimistic UI updates, and stale-while-revalidate data loading.
*   **SignalR Event Hook (`useSignalR`):** Connects to the backend API Gateway via WebSocket connection. Listens for streaming notifications (e.g. `room_ended`, `active_speaker_changed`, `ai_summary_available`) and commits them directly to Zustand stores.
*   **LiveKit WebRTC Integration:** Wraps `livekit-client` SDK to bootstrap camera/mic feeds, handle connection state switches, and route streams to WebRTC components.

### 6. Animations Layer (GSAP & Motion)
*   **GreenSock (GSAP):** Used for advanced layouts, timeline-based entry animations, and landing page visual sequences.
*   **Motion:** Drives fluid transitions, popup modal scaling, dropdown fade-ins, and handles the real-time sliding animation of incoming speech transcripts.

---

## Folder Structure

> Each top-level folder = **independent GitHub repo**

```
WarpTalk - Capstone Project/
├── warptalk-backend/                  ← .NET 10 Gateway + Clean Architecture Services
│   ├── gateway/                       # YARP API Gateway + SignalR Hub + Rate Limiting
│   ├── auth/                          # AuthService (Domain/Application/Infrastructure/API)
│   ├── workspace/                     # WorkspaceService (Domain/Application/Infrastructure/API)
│   ├── translation-room/              # TranslationRoomService (Domain/Application/Infrastructure/API)
│   ├── transcript/                    # TranscriptService (Domain/Application/Infrastructure/API)
│   ├── meeting/                       # MeetingService (Domain/Application/Infrastructure/API)
│   ├── billing/                       # BillingService (Domain/Application/Infrastructure/API)
│   ├── notification/                  # NotificationService (Domain/Application/Infrastructure/API)
│   ├── shared/                        # Shared Protobuf definitions, DTOs, and global helpers
│   ├── test/                          # Unit & integration tests (xUnit, NSubstitute, Testcontainers)
│   └── warptalk-backend.slnx          # Solution manifest
│
├── warptalk-ai/                       ← Python AI Workers
│   ├── shared/                        # Redis client, audio utilities, Protobuf models
│   ├── stt-worker/                    # Speech-to-Text inference
│   ├── translation-worker/            # Translation models and prompt processing
│   ├── tts-worker/                    # Text-to-Speech generation
│   ├── ai-assistant-worker/           # Meeting summaries and document querying (RAG)
│   └── pyproject.toml
│
├── warptalk-web/                      ← Next.js Portal (Frontend)
├── warptalk-desktop/                  ← Electron.js + Virtual Audio Drivers (Desktop client)
│
└── warptalk-infrastructure/           ← DevOps & Infrastructure Orchestration
    ├── docker-compose.yml             # Local dependencies (Postgres, Redis, PgBouncer, RabbitMQ, COTURN)
    ├── docker-compose.dev.yml         # Local development orchestration override
    ├── docker-compose.prod.yml        # Production deployment configurations
    ├── pgbouncer/                     # Connection pooler config
    ├── coturn/                        # STUN/TURN server config
    ├── observability/                 # OTEL, Prometheus, Grafana, Seq configurations
    └── scripts/                       # Startup, seeding, and database initialization scripts
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

Secrets: DB_PASSWORD, REDIS_PASSWORD, JWT_SECRET_KEY, STRIPE_API_KEY,
         STRIPE_WEBHOOK_SECRET, SMTP_PASSWORD, QDRANT_API_KEY,
         GOOGLE_OAUTH_SECRET, AI_MODEL_API_KEYS
```

### Cryptographic Security

*   **Password Hashing:** User passwords are hashed at the Auth Service using **BCrypt** with adaptive unique salt, protecting credentials against brute force and rainbow table attacks.
*   **Document Cryptography (AES-256-CBC + HMAC-SHA512):** When using the `local` storage provider, documents are encrypted at the application layer using **Encrypt-then-MAC** (AES-256-CBC with HMAC-SHA512) utilizing a key derived from the active workspace key. For cloud providers (AWS S3 / MinIO), standard Server-Side Encryption (SSE) is leveraged.

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
