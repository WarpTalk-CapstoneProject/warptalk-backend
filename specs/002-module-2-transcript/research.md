# Architectural Research & Decisions

## 1. Dual Consumer Group Pattern
- **Decision**: The GatewayService and TranscriptService will operate in separate Redis Consumer Groups (`gateway-group` and `transcript-group`).
- **Rationale**: GatewayService needs volatile, high-throughput delivery to broadcast SignalR events to connected clients immediately. TranscriptService needs durable, reliable delivery with acknowledgments to persist segments to PostgreSQL. Separating groups prevents one service from stealing messages from or slowing down the other.

## 2. Idempotency in Persistence
- **Decision**: Use PostgreSQL `INSERT ... ON CONFLICT (id) DO UPDATE` (UPSERT) for TranscriptSegments and Translations.
- **Rationale**: Redis Stream consumers may crash before `XACK`. Upon restart, unacknowledged messages are replayed via `XPENDING`. If we don't use UPSERT, replays will cause `Unique Constraint Violation` errors or duplicate segment inserts.

## 3. Strict Domain Enums
- **Decision**: Define strict C# Constants/Enums (`TranscriptStatus.Active`, etc.) that map exactly to PostgreSQL `UPPERCASE` ENUMs.
- **Rationale**: Gap analysis of the legacy DBML showed a mismatch where domain logic was using lowercase strings while the database used uppercase ENUMs. This resolves the gap and prevents runtime database errors.

## 4. Cross-Service Authorization
- **Decision**: Use gRPC for synchronous cross-service lookups (e.g., `TranslationRoomService.GetParticipantsByRoomId`).
- **Rationale**: The Transcript API must verify if the caller `userId` is a member of the room before allowing them to fetch or export the transcript. Since participants are managed by `TranslationRoomService`, a fast, typed gRPC call is required to enforce data privacy.
