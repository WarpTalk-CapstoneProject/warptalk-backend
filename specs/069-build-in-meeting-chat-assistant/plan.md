# 069 Plan: In-Meeting Multilingual Chat with WarpBot

## 1. Scope Positioning
This feature belongs to the meeting collaboration layer. It complements realtime speech translation but does not replace the audio pipeline.

Recommended module ownership:
* **MeetingService** owns chat session, message persistence, realtime chat delivery, and moderation.
* **AIService / AI Workers** own translation generation and WarpBot response generation.
* **TranscriptService** can link chat history to transcript/meeting history after meeting end.
* **NotificationService** can optionally notify mentions if the user is not active.

## 2. Architecture Decision
Use a hybrid realtime + async pipeline:

1. User sends chat message to Gateway/MeetingService.
2. MeetingService persists original message immediately.
3. Gateway/SignalR broadcasts original message to active room participants.
4. If translation is enabled, MeetingService publishes an async translation job.
5. AI worker returns translated chat content.
6. MeetingService persists translation and broadcasts translation update.
7. If message contains `@warpbot`, MeetingService creates assistant request and publishes AI assistant job.
8. WarpBot response is persisted as an assistant chat message and broadcast to the room.

## 3. API Plan

### MeetingService REST APIs
* `GET /api/v1/meetings/rooms/{roomId}/chat/messages`
  * list room chat messages with pagination
* `POST /api/v1/meetings/rooms/{roomId}/chat/messages`
  * send message
* `POST /api/v1/meetings/rooms/{roomId}/chat/messages/{messageId}/translate`
  * request translation for one message
* `DELETE /api/v1/meetings/rooms/{roomId}/chat/messages/{messageId}`
  * host hides/deletes message
* `GET /api/v1/meetings/rooms/{roomId}/chat/messages/{messageId}/translations`
  * list translations for a message

### Gateway / SignalR Events
* Client -> Server:
  * `SendMeetingChatMessage`
  * `RequestChatMessageTranslation`
  * `HideMeetingChatMessage`
* Server -> Client:
  * `MeetingChatMessageReceived`
  * `MeetingChatMessageTranslationReceived`
  * `MeetingChatAssistantResponsePending`
  * `MeetingChatAssistantResponseReceived`
  * `MeetingChatMessageHidden`

## 4. Message Contract

### Chat message created
```json
{
  "message_id": "uuid",
  "room_id": "uuid",
  "meeting_id": "uuid",
  "workspace_id": "uuid",
  "sender_user_id": "uuid",
  "participant_id": "uuid",
  "sender_display_name": "Host",
  "sender_type": "user",
  "message_type": "text",
  "original_language": "vi-VN",
  "original_text": "Hôm nay mình bàn về .NET 4.7",
  "translation_enabled": true,
  "contains_warpbot_mention": false,
  "created_at": "2026-05-28T10:00:00Z"
}
```

### Chat translation result
```json
{
  "translation_id": "uuid",
  "message_id": "uuid",
  "room_id": "uuid",
  "source_language": "vi-VN",
  "target_language": "en-US",
  "translated_text": "Today we are discussing .NET 4.7",
  "model_used": "nllb-200-distilled-600M",
  "confidence": 0.92,
  "created_at": "2026-05-28T10:00:01Z"
}
```

### WarpBot request
```json
{
  "request_id": "uuid",
  "trigger_message_id": "uuid",
  "room_id": "uuid",
  "workspace_id": "uuid",
  "requested_by_user_id": "uuid",
  "prompt": "is .NET Framework 4.7 still suitable for this project?",
  "context_scope": "current_meeting",
  "status": "queued"
}
```

## 5. UI Plan

### Meeting Page
* Add chat icon/action in meeting control overlay.
* Chat panel opens as right drawer or floating panel.
* Chat message composer:
  * text input
  * send button
  * translation toggle
  * visible hint for `@warpbot`
* Message item:
  * sender
  * timestamp
  * original text
  * translated text if available
  * original/translation toggle
  * assistant badge for WarpBot messages
* Host moderation:
  * hide/delete menu for host
* Empty state:
  * "No messages yet"
* Pending state:
  * "Translating..."
  * "WarpBot is thinking..."

## 6. Data Persistence Plan
* Store original messages immediately.
* Store translations as separate records.
* Store WarpBot request lifecycle separately from chat messages.
* Store WarpBot final response as a chat message with `sender_type = assistant`.
* Store moderation as append-only events.

## 7. Security Plan
* Validate user is active participant before read/write.
* Reject requests from kicked/removed/left participants.
* Host-only moderation.
* Strip/escape HTML to prevent XSS.
* Rate-limit chat and WarpBot mention usage.
* Prompt context must be scoped to current workspace/room only.
* Audit moderation and assistant requests.

## 8. Implementation Phases

### Phase 1: Backend Foundation
* Add chat entities and DB mappings.
* Add message send/list APIs.
* Add SignalR events for original chat.
* Add permission checks.

### Phase 2: Frontend Chat Panel
* Add chat drawer/panel to meeting page.
* Connect send/list/realtime events.
* Add loading/empty/error states.

### Phase 3: Chat Translation
* Add translation request job/event.
* Add AI worker contract for text chat translation.
* Persist and broadcast translation results.
* UI original/translated toggle.

### Phase 4: WarpBot
* Detect `@warpbot`.
* Create assistant request.
* Build authorized context: current meeting transcript, chat history, room metadata, workspace context library.
* Generate assistant response.
* Persist and broadcast assistant response.

### Phase 5: Moderation, History, Testing
* Host hide/delete.
* Link chat history to transcript/meeting history.
* Add backend/frontend tests.
* Add manual E2E/UAT checklist.

## 9. Risks and Mitigation
* **Risk**: Chat translation slows message delivery.
  * **Mitigation**: Broadcast original first, translate asynchronously.
* **Risk**: WarpBot exposes private workspace context.
  * **Mitigation**: enforce workspace/room context boundary and permission checks.
* **Risk**: Chat spam or prompt abuse.
  * **Mitigation**: rate limiting and usage credit tracking.
* **Risk**: Feature scope bloats meeting module.
  * **Mitigation**: keep attachments/threading/reactions out of scope.

## 10. Verification Plan
* Unit tests for permission checks and message lifecycle.
* Integration tests for send/list/translate/moderate endpoints.
* SignalR test for realtime delivery events.
* AI contract tests for translation and WarpBot response payloads.
* FE build/lint.
* Manual E2E:
  * host creates meeting
  * participant joins
  * host sends original message
  * participant receives message
  * participant sends translated message
  * host sees translation
  * user mentions `@warpbot`
  * assistant response appears
  * host hides message
  * hidden message disappears for participant

