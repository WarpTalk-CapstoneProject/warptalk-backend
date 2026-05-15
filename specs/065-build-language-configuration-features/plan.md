# Implementation Plan: 1.4 Build Language Configuration Features

**Spec Reference**: 065-build-language-configuration-features/spec.md
**Status**: approved

## 1. Scope & Objective

Implement validation logic to ensure that translation rooms and participants adhere strictly to supported language configurations and room-specific policies across all flows (Creation, Join, and Routing).

## 2. Technical Design

### 2.1 Language Validation Helper (`ILanguageValidator`)
- **Purpose**: Centralize language validation logic.
- **Methods**:
    - `Task<bool> IsSupportedAsync(string code)`: Checks if the code exists in `platform.supported_languages`.
    - `bool IsAllowedByPolicy(string code, TranslationRoom room)`: Checks if the code is either the `SourceLanguage` or in the `TargetLanguages` list of the room.
- **Implementation**:
    - Cache the supported languages list for high-performance validation.

### 2.2 Room Configuration Validation (Creation/Update)
- **Target**: `CreateTranslationRoomRequest` and `UpdateRoomSettingsRequest`.
- **Validation**:
    1. `SourceLanguage` is valid.
    2. `TargetLanguages` are all valid.
    3. **Status Check**: Ensure the room is NOT `IN_PROGRESS` before allowing language policy updates.
    4. **Circular Support**: `SourceLanguage` can be included in `TargetLanguages` (e.g., for transcript in original language).

### 2.3 Flow Integration: Join & Routing Awareness
- **Join Flow Integration**: 
    - In `TranslationRoomService.JoinTranslationRoomAsync`, after fetching the room by code, call `ILanguageValidator.IsAllowedByPolicy`.
    - **Note on Defaulting**: API requests will continue to require `SpeakLanguage` and `ListenLanguage`. The Client/Frontend is responsible for fetching defaults from `auth.user_settings` and allowing user confirmation before sending the request.
    - Reject if `SpeakLanguage` or `ListenLanguage` is not allowed by the room policy.
- **Routing Awareness**:
    - Ensure that when the `TranslationRoom` is mapped to a DTO or used in routing logic, the `SourceLanguage` and `TargetLanguages` are correctly populated and used as constraints for any audio route creation.

## 3. Constitution Gates Check

- [x] **Article I (Clean Architecture)**: Validation in Service layer where context (Room Entity) is available.
- [x] **Article III (Standards)**: Handle JSONB `target_languages` correctly in DB mappings.
- [x] **Article IV (TDD)**: Comprehensive unit tests for policy violations.

## 4. Components Affected

### 4.1 Application Layer
- `ILanguageValidator` & `LanguageValidator` (New)
- `TranslationRoomService`: Update `JoinTranslationRoomAsync` and `CreateTranslationRoomAsync`.
- `TranslationRoomParticipantService`: Update `UpdatePreferences` (if exists).

### 4.2 Infrastructure Layer
- `TranslationRoomDbContext`: Ensure `TargetLanguages` maps to a `List<string>` using a value converter for JSONB.

## 5. Verification Plan

### Automated Tests
- `JoinRoom_ShouldReject_WhenSpeakLanguageNotInPolicy`
- `JoinRoom_ShouldReject_WhenListenLanguageNotInPolicy`
- `CreateRoom_ShouldReject_WhenTargetLanguageIsSameAsSource`

### Manual Verification
- Attempt to join a room with a language not configured in that room's target list via Swagger.
