# 069 Data Model: In-Meeting Multilingual Chat with WarpBot

## Table: `meeting.meeting_chat_messages`
Stores original chat messages and assistant responses.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | UUID | Primary key |
| `workspace_id` | UUID | External AuthService workspace id |
| `translation_room_id` | UUID | External TranslationRoomService room id |
| `meeting_id` | UUID | MeetingService meeting/session id |
| `sender_user_id` | UUID | External AuthService user id, nullable for system |
| `participant_id` | UUID | Meeting/room participant id |
| `sender_display_name` | VARCHAR(150) | Snapshot for history |
| `sender_type` | VARCHAR(20) | `user`, `assistant`, `system` |
| `message_type` | VARCHAR(20) | `text`, `assistant_response`, `system_notice` |
| `original_language` | VARCHAR(15) | e.g. `vi-VN`, `en-US`, `ja-JP` |
| `original_text` | TEXT | Original message content |
| `translation_enabled` | BOOLEAN | Whether translation was requested |
| `contains_warpbot_mention` | BOOLEAN | True if message invoked WarpBot |
| `status` | VARCHAR(20) | `sent`, `hidden`, `deleted`, `failed` |
| `created_at` | TIMESTAMPTZ | Created timestamp |
| `updated_at` | TIMESTAMPTZ | Updated timestamp |
| `hidden_at` | TIMESTAMPTZ | Nullable |
| `hidden_by` | UUID | Host/admin user id |

Suggested indexes:
* `(meeting_id, created_at)`
* `(translation_room_id, created_at)`
* `(workspace_id, created_at)`
* `(sender_user_id, created_at)`

## Table: `meeting.meeting_chat_translations`
Stores translated versions of chat messages.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | UUID | Primary key |
| `message_id` | UUID | Internal FK to `meeting_chat_messages.id` |
| `source_language` | VARCHAR(15) | Source language |
| `target_language` | VARCHAR(15) | Target language |
| `translated_text` | TEXT | Translated content |
| `translator_model` | VARCHAR(100) | Model/provider used |
| `confidence` | DECIMAL(5,4) | Nullable |
| `status` | VARCHAR(20) | `queued`, `completed`, `failed` |
| `latency_ms` | INT | Nullable |
| `created_at` | TIMESTAMPTZ | Created timestamp |
| `updated_at` | TIMESTAMPTZ | Updated timestamp |

Suggested indexes:
* `(message_id, target_language)` unique
* `(status, created_at)`

## Table: `meeting.meeting_chat_assistant_requests`
Tracks WarpBot request lifecycle.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | UUID | Primary key |
| `workspace_id` | UUID | External AuthService workspace id |
| `translation_room_id` | UUID | External TranslationRoomService room id |
| `meeting_id` | UUID | Meeting/session id |
| `trigger_message_id` | UUID | Chat message that invoked `@warpbot` |
| `requested_by_user_id` | UUID | External AuthService user id |
| `prompt` | TEXT | User prompt after mention normalization |
| `context_scope` | VARCHAR(30) | `current_meeting`, `workspace_context`, `transcript` |
| `status` | VARCHAR(20) | `queued`, `processing`, `completed`, `failed`, `cancelled` |
| `response_message_id` | UUID | Chat message id for assistant response |
| `model_provider` | VARCHAR(50) | Nullable |
| `model_name` | VARCHAR(100) | Nullable |
| `input_tokens` | INT | Nullable |
| `output_tokens` | INT | Nullable |
| `latency_ms` | INT | Nullable |
| `error_code` | VARCHAR(100) | Nullable |
| `error_message` | TEXT | Nullable |
| `created_at` | TIMESTAMPTZ | Created timestamp |
| `completed_at` | TIMESTAMPTZ | Nullable |

Suggested indexes:
* `(meeting_id, created_at)`
* `(trigger_message_id)`
* `(requested_by_user_id, created_at)`
* `(status, created_at)`

## Table: `meeting.meeting_chat_moderation_events`
Append-only record for host/admin moderation actions.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | UUID | Primary key |
| `message_id` | UUID | Internal FK to chat message |
| `workspace_id` | UUID | External AuthService workspace id |
| `meeting_id` | UUID | Meeting/session id |
| `action` | VARCHAR(30) | `hide`, `delete`, `restore` |
| `reason` | VARCHAR(500) | Nullable |
| `performed_by` | UUID | Host/admin user id |
| `created_at` | TIMESTAMPTZ | Created timestamp |

Suggested indexes:
* `(message_id, created_at)`
* `(meeting_id, created_at)`
* `(performed_by, created_at)`

## Ownership Notes
* MeetingService owns chat tables because chat is part of live meeting collaboration.
* AIService owns model execution but not chat persistence.
* TranscriptService can reference chat history after meeting finalization.
* Workspace/user IDs are external references and should not be physical cross-service FKs.

## Status Enums

### Message Status
* `sent`
* `hidden`
* `deleted`
* `failed`

### Translation Status
* `queued`
* `completed`
* `failed`

### Assistant Request Status
* `queued`
* `processing`
* `completed`
* `failed`
* `cancelled`

