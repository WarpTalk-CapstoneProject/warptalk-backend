# Feature Specification: 1.4 Build Language Configuration Features

**Feature Branch**: `feature/wt-65-14-build-language-configuration-features`
**Created**: 2026-05-15
**Status**: approved
**Input**: Linear Ticket WT-65

## Description

Implement language configuration and validation for Translation Rooms and Participants. This ensures that all translation routes are predictable, supported by the platform, and adhere to the specific policy defined for each room.

## User Scenarios & Testing

**User Story**: As a Host and Participant, I want room and participant language settings to be validated so that translation routes are predictable and supported.

**Acceptance Scenarios**:

1. **Room Creation**: Host configures a valid source language and one or more target languages. The system validates these against the platform's supported list.
2. **Participant Join**: A participant joins and selects a speaking and listening language. The system ensures these choices are valid and allowed by the room's specific language policy.
3. **Invalid Configuration**: Any attempt to use an unsupported language or a language outside the room's policy results in a clear 400 Bad Request error.

## Business Rules *(Xác nhận rule nghiệp vụ)*

### 1. Platform Supported Languages
- All language codes (source, target, speak, listen) MUST exist in the `platform.supported_languages` table.
- Language codes follow the BCP-47 format (e.g., `vi-VN`, `en-US`, `ja-JP`).

### 2. Room Language Policy
- **Source Language**: The primary language intended to be spoken in the room. (Field: `source_language`)
- **Target Languages**: The list of languages available for translation in the room. (Field: `target_languages`)
- **Policy Stability**: Room language policy (Source/Targets) CANNOT be updated once the room is `IN_PROGRESS`. Updates are only allowed in `SCHEDULED` or `WAITING` states.
- **Circular Support**: `source_language` MAY be included in the `target_languages` list if the user wishes to receive the original language transcript/audio as a "target".
- **Mode Determination**:
    - **Single-language Mode**: Room has exactly ONE target language.
    - **Multi-language Mode**: Room has MULTIPLE target languages.

### 3. Participant Language Constraints
- **Speak Language**: Must be one of the languages defined in the room's policy (Source or Targets).
- **Listen Language**: Must be one of the languages defined in the room's policy (Source or Targets).
- **Rule**: `speak_language` and `listen_language` MUST be contained within the set `{SourceLanguage} ∪ {TargetLanguages}`.

## Flow Integration *(Yêu cầu tích hợp luồng)*

### A. Join Flow (Available to Join)
- The system MUST fetch the room's `SourceLanguage` and `TargetLanguages` during the Join process.
- **Language Initialization**:
    - For registered users, `speak_language` and `listen_language` SHOULD be initialized from `auth.user_settings.default_speak_language` and `auth.user_settings.default_listen_language`.
    - The UI MUST remind and allow the user to confirm or change these settings before joining.
- **Validation**:
    - If the participant's final requested `SpeakLanguage` or `ListenLanguage` is not in the room's policy, the join MUST be rejected.
- **Why**: This ensures a smooth UX for registered users while maintaining strict adherence to room rules.

### B. Translation Routing (Routing Awareness)
- Any internal logic responsible for creating `TranslationRoomAudioRoute` or setting up STT/Translation/TTS pipelines MUST read the saved room policy.
- Routes can ONLY be created between languages explicitly defined in the room's `source_language` and `target_languages`.
- **Why**: Ensures consistency between what the user configured and what the AI/Media engines are actually doing.

## Data Model *(Rà schema/data model)*

### TranslationRoom (`translation_room.translation_rooms`)
- `source_language`: `VARCHAR(15)` - Stores a single language code (e.g., `vi-VN`).
- `target_languages`: `JSONB` - Stores an array of language codes (e.g., `["en-US", "ja-JP"]`).

### TranslationRoomParticipant (`translation_room.translation_room_participants`)
- `speak_language`: `VARCHAR(15)` - The language the participant will speak.
- `listen_language`: `VARCHAR(15)` - The language the participant wants to hear (translated).

## Validation Logic *(Thiết kế validation rule)*

### Room-level Logic
- **Validate Platform Support**: Check `source_language` and every item in `target_languages` against `platform.supported_languages`.
- **Status Check**: Reject updates if room status is `IN_PROGRESS`.
- **Reject** if `source_language` is null or empty.
- **Reject** if `target_languages` is empty.
- **Allow Circular Support**: `source_language` is allowed to be the same as any language in `target_languages`. Duplicate languages within `target_languages` will be ignored or accepted based on current code behavior.

### Participant-level Logic
- **On Join/Update Preferences**:
    - Validate `speak_language` and `listen_language` are supported by the platform.
    - **Policy Check**: Validate that both `speak_language` and `listen_language` exist in the room's `{source_language} + target_languages` set.

## Implementation Plan

### 1. Validator/Helper
- [x] Tách validator/helper dùng chung (`ILanguagePolicy`, `LanguageHelper`)
- [x] check supported language
- [x] normalize/compare language code nếu cần
- [x] validate room policy
- [x] validate participant language config

### 2. Service Integration
- Update `TranslationRoomService.JoinTranslationRoomAsync` to perform the policy check after fetching the room entity.
- Update `TranslationRoomService.CreateTranslationRoomAsync` to validate the room-level policy against platform data.

### 3. Error Handling
- Use `ErrorCodes.ValidationError` (400 Bad Request).
- Clear messages indicating exactly which language violated which policy.

## Verification Plan

### Automated Tests
- `ParticipantJoin_ShouldFail_WhenLanguageNotInRoomPolicy`
- `RoomCreation_ShouldFail_WhenSourceUnsupported`
- `TranslationRouting_ShouldRespectRoomPolicy` (Mock routing logic check)
