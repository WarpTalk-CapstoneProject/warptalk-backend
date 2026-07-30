# Tasks: 1.4 Build Language Configuration Features

**Spec Reference**: 065-build-language-configuration-features/spec.md
**Status**: in-progress

## Phase 0: TDD & Data Setup
- `[ ]` Create `LanguageConfigurationTests.cs` in Tests project.
- `[ ]` Test: Room creation fails with unsupported source language.
- `[ ]` Test: Room creation fails with unsupported target language.
- `[ ]` Test: Participant join fails with language outside room policy.
- `[ ]` Test: Participant join succeeds with allowed languages.
- `[ ]` Run tests and confirm FAIL.

## Phase 1: Infrastructure & Shared
- `[ ]` Verify `TranslationRoomDbContext` handles `TargetLanguages` JSONB array correctly (use `HasConversion` if needed).
- `[ ]` Implement `ISupportedLanguageRepository` to fetch valid codes from `platform.supported_languages`.

## Phase 2: Core Validation Logic
- `[ ]` Implement `ILanguageValidator` in Application layer.
- `[ ]` Logic to check if a code is in the platform supported list (cached).
- `[ ]` Logic to check if a code is allowed by a specific `TranslationRoom` policy.

## Phase 3: Application & API Layer Integration
- `[ ]` Integrate `ILanguageValidator` into `CreateTranslationRoomRequestValidator`.
- `[ ]` Integrate `ILanguageValidator` into `JoinTranslationRoomRequestValidator`.
- `[ ]` Update `TranslationRoomService` and `TranslationRoomParticipantService` to return appropriate `Result.Failure` with clear messages.

## Phase 4: Verification & Polish
- `[ ]` Run all tests (Unit + Integration).
- `[ ]` Verify `vi-VN`, `en-US`, `ja-JP` examples work as expected.
- `[ ]` Check Swagger documentation for correct error responses.
- `[ ]` Mark spec/plan as completed.
