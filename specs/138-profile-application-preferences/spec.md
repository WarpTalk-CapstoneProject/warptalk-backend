# Feature Specification: Profile and Application Preferences Management (WT-138)

**Feature Branch**: `feat/auth-profile-preferences`  
**Created**: 2026-05-22  
**Status**: Approved  
**Input**: Linear ticket WT-138 - [Auth] Let users manage profile and application preferences

---

## 1. Problem Statement

An authenticated WarpTalk user needs to be able to retrieve and update their core profile details and application preferences. These preferences control fundamental client-side and downstream server-side behaviors such as translation default languages, room configuration defaults, transcript display settings, UI theme, and accessibility features (high contrast, screen reader modes).

Without a robust preferences system:
1. **Bad Downstream Defaults**: Users would have to manually configure their speaking/listening languages and audio/recording options every time they join or create a Translation Room, creating friction.
2. **Lack of Personalization**: Important accessibility (font size, high contrast) and UI preference (theme, compact mode) settings would not persist across sessions.
3. **Downstream Corruption Risk**: If invalid or malformed preferences (e.g. invalid language codes, incorrect room types, or extreme font sizes) are stored, it can corrupt downstream microservices (like `TranslationRoomService` or `TranscriptService`) when they read these values to initialize rooms or process media streams.
4. **Orphaned User Settings**: For existing database users or newly registered users, if the `UserSetting` record is not automatically created, settings retrievals will fail, resulting in empty or crashed UI states.

---

## 2. Technical Decisions & Architectural Boundaries

### 2.1. Domain Segregation (Profile vs. Preferences)
To keep the domain models clean and maintainable, user settings are structurally separated from the core user identity:
- **User Profile Fields** (stored directly in `auth.users`):
  - `full_name` (string, max 150)
  - `phone` (string, max 20, nullable)
  - `preferred_language` (string, BCP-47, default `vi-VN`)
  - `timezone` (string, IANA timezone ID, default `Asia/Ho_Chi_Minh`)
- **User Settings Preferences** (stored in `auth.user_settings`):
  - UI Preferences: `theme` (`light`/`dark`/`system`)
  - Language Defaults: `default_speak_language` (BCP-47), `default_listen_language` (BCP-47)
  - Audio Settings: `voice_clone_enabled` (boolean), `mic_noise_suppression` (boolean)
  - Room Defaults: `default_translation_room_type` (one of 2 types: instant or scheduled), `auto_record_translation_rooms` (boolean), `auto_generate_summary` (boolean), `default_max_participants` (1 to 500)
  - Transcript Settings: `transcript_font_size` (10 to 32), `show_original_transcript` (boolean), `show_translated_transcript` (boolean)
  - Accessibility Settings: `high_contrast` (boolean), `screen_reader_mode` (boolean)

### 2.2. Validation Rules & Data Sanity
To prevent downstream system corruption, updates to user settings will be strictly validated in the Application layer before being committed to the database:
- **Language Validation**: `default_speak_language` and `default_listen_language` must be valid BCP-47 formats (e.g., `vi-VN`, `en-US`, `ja-JP`).
- **Timezone Validation (Production-Grade Standard)**:
  - **Storage Standard**: All timezones in the database MUST be saved in the **IANA Timezone ID** format (e.g., `Asia/Ho_Chi_Minh`, `America/New_York`, `UTC`). Windows Timezone IDs (e.g., `SE Asia Standard Time`) are strictly disallowed in the storage layer.
  - **Validation Method**: We will use C# native `TimeZoneInfo.FindSystemTimeZoneById(timezone)` on .NET 10. This method automatically resolves IANA IDs cross-platform (under standard globalization modes).
  - **Deployment Environment Prerequisites**:
    - **Windows Hosts**: Must NOT enable globalization-invariant mode or custom NLS modes that bypass standard ICU mappings (which would break IANA resolution).
    - **Linux/Docker Containers**: The hosting environment must contain the standard International Components for Unicode (`icu` or `icu-libs`) and the timezone database (`tzdata`). (Standard `aspnet:10.0` Debian-based base images contain this out of the box; if custom Alpine-slim is ever adopted, they must be explicitly added via package manager).
  - **Legacy System Integration mapping**: If integration with external Windows-only services is required, the application layer MUST use .NET's native `TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId)` utility rather than hardcoding static conversion tables. This ensures compliance with standard timezone updates.
- **Numeric Limits**:
  - `default_max_participants`: Must be strictly between `1` and `500`.
  - `transcript_font_size`: Must be strictly between `10` and `32` (pixels).
- **String Enums**:
  - `theme`: Must be one of `light`, `dark`, or `system`.
  - `default_translation_room_type`: Must be one of `instant` or `scheduled`.

### 2.3. Self-Healing Settings Provisioning (Default Setup)
- **Automatic Creation**: When a new user is created (e.g., via registration or external Google sign-in), the system **MUST** automatically create a default `UserSetting` record for them.
- **Lazy Initialization / Self-Healing**: If a user is fetched but doesn't have an associated `UserSetting` record (e.g., legacy users created before Sprint 2), the system will **automatically initialize and save** a new default `UserSetting` record on-the-fly during retrieval, ensuring that the client application always receives valid settings.

### 2.4. Decoupled Service Boundary
To adhere to the Single Responsibility Principle, we will introduce a new dedicated service:
- **`IUserSettingsService` / `UserSettingsService`**: Handles user settings retrieval and updates, completely decoupled from `ProfileService`.
- **`UserSettingsController`**: Exposes the `/api/auth/settings` REST endpoints.

This decoupled boundary strictly addresses two critical design requirements:
1. **Business Logic Segregation (Nghiệp vụ độc lập)**: Profile management focuses solely on primary user identity (names, phones, secure password verification). Application preferences comprise entirely distinct domain logic (translation preferences, UI styling, and accessibility controls). By isolating this, the system is fully prepared to absorb future specialized preference domains—such as **Notification settings** (e.g., configuring `notification_preferences` table settings), **Privacy configuration**, and **Linked accounts** management (e.g., Google OAuth links from WT-137)—into a cohesive "User Preference & Integration" module without bloating the core profile layer.
2. **API & Permission Differentiation (Phân quyền & Khác biệt API)**: The User Profile API (`/api/auth/me`) deals with sensitive demographic data and credential-altering actions. Conversely, the settings API is designed to be low-barrier and frequently called by the front-end (for auto-saving settings/preferences on click). Isolating Settings into its own controller allows for flexible, granular permission sets (e.g., applying workspace-level limits, default settings profiles, or subscription tier-based configuration rules) completely independent of profile access rules.

---

## 3. API Contract Notes

### 3.1. Get User Settings
Retrieve the settings for the authenticated user.

- **URL**: `GET /api/auth/settings`
- **Headers**: `Authorization: Bearer <token>`
- **Response**: `200 OK`
```json
{
  "defaultSpeakLanguage": "vi-VN",
  "defaultListenLanguage": "en-US",
  "voiceCloneEnabled": false,
  "micNoiseSuppression": true,
  "defaultTranslationRoomType": "instant",
  "autoRecordTranslationRooms": false,
  "autoGenerateSummary": true,
  "defaultMaxParticipants": 10,
  "theme": "system",
  "transcriptFontSize": 14,
  "showOriginalTranscript": true,
  "showTranslatedTranscript": true,
  "compactParticipantList": false,
  "highContrast": false,
  "screenReaderMode": false,
  "updatedAt": "2026-05-22T11:15:00Z"
}
```

### 3.2. Update User Settings
Update one or more settings for the authenticated user. Partial updates (patch-like behavior) are supported.

- **URL**: `PUT /api/auth/settings`
- **Headers**:
  - `Authorization: Bearer <token>`
  - `Content-Type: application/json`
- **Request Body**:
```json
{
  "defaultSpeakLanguage": "vi-VN",
  "defaultListenLanguage": "ja-JP",
  "voiceCloneEnabled": true,
  "micNoiseSuppression": true,
  "defaultTranslationRoomType": "scheduled",
  "autoRecordTranslationRooms": true,
  "autoGenerateSummary": true,
  "defaultMaxParticipants": 50,
  "theme": "dark",
  "transcriptFontSize": 16,
  "showOriginalTranscript": true,
  "showTranslatedTranscript": true,
  "compactParticipantList": true,
  "highContrast": false,
  "screenReaderMode": false
}
```
- **Response**: `200 OK` (Returns the updated settings object).
- **Error Responses**:
  - `400 Bad Request` with `ValidationError` code when validation fails (e.g. invalid font size, unsupported language, etc.).
  - `401 Unauthorized` when the token is missing or invalid.

---

## 4. User Scenarios & Testing (Prioritized Journeys)

### User Story 1 - Profile Completion & Details Read/Update (Priority: P1)

*As an authenticated user, I want to read and update my profile details (Full Name, Phone, preferred language, timezone) so that my contact details and localization options are correct.*

**Why this priority**: Fundamental user identity and timezone matching which is crucial for meeting schedulers.

**Independent Test**: Fetch and update profile details via `GET /api/auth/me` and `PUT /api/auth/me`, asserting that the fields are updated correctly in the database.

**Acceptance Scenarios**:
1. **Given** a user is logged in with active token,  
   **When** they send `GET /api/auth/me`,  
   **Then** the system returns their full profile including `fullName`, `phone`, `preferredLanguage`, and `timezone`.
2. **Given** a user is logged in,  
   **When** they send `PUT /api/auth/me` with:  
   `fullName = "John Doe"`, `phone = "+84987654321"`, `preferredLanguage = "en-US"`, `timezone = "Asia/Ho_Chi_Minh"`,  
   **Then** the system updates these details and returns `200 OK` with the updated profile.
3. **Given** a user is logged in,  
   **When** they send `PUT /api/auth/me` with an invalid timezone (e.g., `Invalid/Timezone`),  
   **Then** the system **REJECTS** the request with a `400 Bad Request` and error code `ValidationError`.

---

### User Story 2 - User Settings Retrieval & Update (Priority: P1)

*As an authenticated user, I want to manage my application preferences so that my voice clone, theme, and default room settings are synchronized and persisted.*

**Why this priority**: Core settings capability required for downstream room joins and real-time accessibility.

**Independent Test**: Call the get and update endpoints on `/api/auth/settings` and verify database persistence.

**Acceptance Scenarios**:
1. **Given** an authenticated user who has settings configured,  
   **When** they call `GET /api/auth/settings`,  
   **Then** the system returns their exact settings from the database.
2. **Given** an authenticated user,  
   **When** they call `PUT /api/auth/settings` with valid preferences:     `theme = "dark"`, `transcriptFontSize = 18`, `defaultSpeakLanguage = "vi-VN"`, `defaultTranslationRoomType = "instant"`,  
   **Then** the system saves the settings, updates the `updatedAt` timestamp, and returns the full updated settings.

---

### User Story 3 - Automatic & Self-Healing Settings Creation (Priority: P2)

*As a newly registered WarpTalk user, I want my settings to be automatically initialized with system defaults so that I do not encounter any loading errors when I first open my settings page.*

**Why this priority**: Prevents system crashes and empty states for new and legacy users.

**Independent Test**: Register a new user, immediately fetch their settings, and verify that default values are returned and persisted.

**Acceptance Scenarios**:
1. **Given** a new user registers via email/password or Google SSO,  
   **Then** the system automatically inserts a corresponding `UserSetting` record with system defaults:  
   - `default_speak_language` = `vi-VN`
   - `default_listen_language` = `en-US`
   - `theme` = `system`
   - `default_translation_room_type` = `instant`
   - `transcript_font_size` = `14`
2. **Given** a legacy user in the database without any existing `UserSetting` record,  
   **When** they call `GET /api/auth/settings`,  
   **Then** the system dynamically instantiates, saves, and returns the default settings record (Self-healing).

---

### User Story 4 - Error States & Validation Safeguard (Priority: P2)

*As WarpTalk, I want the system to validate all settings updates strictly so that downstream translation and audio routing behaviors are never corrupted by invalid values.*

**Why this priority**: Security and reliability guard to protect real-time room communication from crashes.

**Independent Test**: Attempt updates with out-of-bounds or invalid values and verify rejection.

**Acceptance Scenarios**:
1. **Given** an authenticated user,  
   **When** they attempt `PUT /api/auth/settings` with `transcriptFontSize = 8` (below minimum `10`),  
   **Then** the system **REJECTS** with `400 Bad Request` and `ValidationError`.
2. **Given** an authenticated user,  
   **When** they attempt `PUT /api/auth/settings` with `transcriptFontSize = 35` (above maximum `32`),  
   **Then** the system **REJECTS** with `400 Bad Request` and `ValidationError`.
3. **Given** an authenticated user,  
   **When** they attempt `PUT /api/auth/settings` with `defaultTranslationRoomType = "invalid_type"`,  
   **Then** the system **REJECTS** with `400 Bad Request` and `ValidationError`.

---

## 5. Requirements

### Functional Requirements
- **FR-138-001**: System MUST expose `GET /api/auth/settings` to retrieve the authenticated user's settings.
- **FR-138-002**: System MUST expose `PUT /api/auth/settings` to update settings for the authenticated user.
- **FR-138-003**: The settings get/update features MUST require JWT authentication. A user MUST only access or update their own settings.
- **FR-138-004**: During user registration or first-time social sign-in, the system MUST provision default user settings.
- **FR-138-005**: If a settings retrieval request is made for a user who does not have settings, the system MUST dynamically initialize and save the default settings in the database.
- **FR-138-006**: Timezone updates in profile MUST be validated against the list of OS-installed IANA timezone IDs using C# `TimeZoneInfo`.
- **FR-138-007**: Settings update MUST validate limits:
  - `theme`: `light`, `dark`, or `system`.
  - `default_translation_room_type`: `instant` or `scheduled`.
  - `transcript_font_size`: between `10` and `32`.
  - `default_max_participants`: between `1` and `500`.
- **FR-138-008**: Validation failure MUST return a `400 Bad Request` with error code `ValidationError` and a list of invalid fields.

---

## 6. Success Criteria & Metrics

### Measurable Outcomes
- **SC-138-001**: **100% Settings Provisioning**: All new users registered have a matching settings record.
- **SC-138-002**: **No downstream crashes**: Zero invalid language or timezone configurations saved to the database.
- **SC-138-003**: **Sub-200ms latency**: Get and Update settings endpoints respond in less than 200ms on average under standard loads.

---

## 7. Assumptions
- The front-end is responsible for displaying valid lists of language options (from `platform.supported_languages` metadata) to the user.
- System-supported timezones correspond to standard system timezones in the deployment container environment.
