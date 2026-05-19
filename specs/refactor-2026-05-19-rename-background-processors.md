# Refactor: Rename and Refactor Background Processors in Translation Room Service
Date: 2026-05-19
Author: Antigravity

## What is being refactored
- Interfaces in `WarpTalk.TranslationRoomService.Application/Interfaces`:
  - `ITelemetryProcessorService.cs` $\rightarrow$ Rename to `ITelemetryProcessor.cs` (Interface: `ITelemetryProcessor`)
  - `IArtifactsFinalizationService.cs` $\rightarrow$ Rename to `IArtifactsFinalizer.cs` (Interface: `IArtifactsFinalizer`)
- Classes in `WarpTalk.TranslationRoomService.Application/BackgroundProcessors`:
  - `TelemetryProcessorService.cs` $\rightarrow$ Rename to `TelemetryProcessor.cs` (Class: `TelemetryProcessor` implements `ITelemetryProcessor`)
  - `ArtifactsFinalizationService.cs` $\rightarrow$ Rename to `ArtifactsFinalizer.cs` (Class: `ArtifactsFinalizer` implements `IArtifactsFinalizer`)
- Usages and Registrations:
  - `TelemetryRedisSubscriber.cs` in `Infrastructure/Redis`
  - `ArtifactsFinalizationWorker.cs` in `API/Workers`
  - `Program.cs` in `API`
  - Unit Tests: `TelemetryProcessorServiceTests.cs` (Rename file to `TelemetryProcessorTests.cs`, rename class to `TelemetryProcessorTests`)

## Why
- **Architectural Clarity & Naming Conventions**: Background processors/handlers are fundamentally different from standard, HTTP/gRPC API Services (`TranslationRoomService`, etc.) in our Clean Architecture setup. Using the suffix `Service` for background event processing/telemetry calculations creates massive confusion for developers.
- **Clean Interface design**:
  - `ITelemetryProcessor` doesn't need to return `Result` wrapping (which is a standard API boundary return type). It can execute as a background task.
  - `IArtifactsFinalizer` should only expose the entry point `ProcessRoomFinalizationAsync`. The internal `FinalizeRoomArtifactsAsync` should be private/internal.
  - Internal methods inside `ArtifactsFinalizer` (`FinalizeTranscriptAsync`, `FinalizeSummaryAsync`, `FinalizeRecordingAsync`) shouldn't wrap their outputs in `Result<T>` and check `.IsSuccess` only to manually throw an exception. Instead, they should return the entity directly and throw exceptions on failure, keeping implementation clean and avoiding anti-patterns.

## What does NOT change
- Business logic, Telemetry computation algorithms (warm-up sequence, EMA calculation, Hysteresis rules).
- Redis telemetry TTL and key storage rules.
- State transitions triggered during telemetry updates or artifacts finalization.

## Clean Architecture Compliance Check
- [x] Still follows Clean Architecture (Domain $\rightarrow$ Application $\rightarrow$ Infrastructure $\rightarrow$ API)?
- [x] Redis event handling unchanged?
- [x] Build integrity preserved?
