# Implementation Plan: Real-time Translation & Transcript (Module 2)

**Branch**: `002-module-2-transcript` | **Date**: 2026-05-14 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/002-module-2-transcript/spec.md`

## Summary

Implement the backend persistence, API, and cross-service integration for WarpTalk Module 2. The solution uses a BackgroundService for dual-group consumption of Redis Streams (`stt:results`, `translate:results`) with idempotent upserts to PostgreSQL (via PgBouncer). Cross-service gRPC calls handle participant permissions, billing, and identity resolution.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: EF Core 10, StackExchange.Redis, Grpc.AspNetCore
**Storage**: PostgreSQL 18 (via PgBouncer), Redis 7 (Streams)
**Testing**: xUnit, Testcontainers (PostgreSQL, Redis)
**Target Platform**: Linux containers (Docker)
**Project Type**: Microservice backend (TranscriptService)
**Performance Goals**: 500+ segments/second consumer throughput
**Constraints**: Requires idempotent operations (`ON CONFLICT`) to handle Redis consumer replays without duplicating segments
**Scale/Scope**: Enterprise-grade, supporting long-running translation rooms with multiple target languages

## Constitution Check

*GATE: Passed. Architecture adheres to Clean Architecture, no tight coupling between domains, Redis Streams used properly for decoupled AI result broadcast vs persistence.*

## Project Structure

### Documentation (this feature)

```text
specs/002-module-2-transcript/
├── plan.md              # This file
├── research.md          # Architectural decisions (Dual Consumer, Idempotency)
├── data-model.md        # DB schemas (Transcripts, Segments, Translations)
├── quickstart.md        # Local execution guide
├── contracts/           # Updated .proto files for cross-service RPC
└── tasks.md             # (Created in next step)
```

### Source Code (repository root)

```text
warptalk-backend/transcript/src/
├── WarpTalk.TranscriptService.Domain/
│   └── Entities/             # Transcript, TranscriptSegment, etc.
│   └── Constants/            # TranscriptStatus
├── WarpTalk.TranscriptService.Infrastructure/
│   ├── Persistence/          # EF Core DbContext, Repositories
│   └── Redis/                # TranscriptRedisConsumerService
├── WarpTalk.TranscriptService.Application/
│   ├── Services/             # SegmentService, CorrectionService
│   └── DTOs/                 # Request/Response models
├── WarpTalk.TranscriptService.API/
│   ├── Controllers/          # REST Endpoints
│   └── Grpc/                 # TranscriptGrpcService

warptalk-backend/shared/WarpTalk.Shared/
└── Protos/                   # transcript.proto, translation_room.proto updates
```

**Structure Decision**: Extending the existing Clean Architecture layout of `TranscriptService`. Cross-cutting gRPC protos belong in the `warptalk-backend/shared/` project.
