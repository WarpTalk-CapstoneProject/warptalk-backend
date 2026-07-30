# 069 Tasks: In-Meeting Multilingual Chat with WarpBot

## Backend - MeetingService
- [ ] Define `MeetingChatMessage` entity.
- [ ] Define `MeetingChatTranslation` entity.
- [ ] Define `MeetingChatAssistantRequest` entity.
- [ ] Define `MeetingChatModerationEvent` entity.
- [ ] Add EF mappings and migrations.
- [ ] Implement participant permission guard for chat read/write.
- [ ] Implement `GET /meetings/rooms/{roomId}/chat/messages`.
- [ ] Implement `POST /meetings/rooms/{roomId}/chat/messages`.
- [ ] Implement `POST /meetings/rooms/{roomId}/chat/messages/{messageId}/translate`.
- [ ] Implement host-only hide/delete endpoint.
- [ ] Broadcast `MeetingChatMessageReceived`.
- [ ] Broadcast `MeetingChatMessageTranslationReceived`.
- [ ] Broadcast `MeetingChatAssistantResponsePending`.
- [ ] Broadcast `MeetingChatAssistantResponseReceived`.
- [ ] Broadcast `MeetingChatMessageHidden`.

## Backend - AI / Worker Contract
- [ ] Define chat translation job payload.
- [ ] Define chat translation result payload.
- [ ] Define WarpBot request payload.
- [ ] Define WarpBot response payload.
- [ ] Ensure `message_id` remains stable and translations reference original message.
- [ ] Ensure assistant context is scoped to room/workspace.

## Frontend
- [ ] Add chat action to meeting control overlay.
- [ ] Build in-meeting chat panel/drawer.
- [ ] Build message list.
- [ ] Build composer with translation toggle.
- [ ] Detect/visualize `@warpbot` mention.
- [ ] Connect message list API.
- [ ] Connect realtime chat events.
- [ ] Show translated text when ready.
- [ ] Add original/translated view toggle.
- [ ] Show WarpBot pending and response states.
- [ ] Add host moderation menu.
- [ ] Add empty/loading/error states.
- [ ] Remove all mock chat data from production meeting path.

## Transcript / History Integration
- [ ] Link chat history to meeting history.
- [ ] Include chat references in transcript detail if policy allows.
- [ ] Ensure chat retention follows workspace policy.

## Security and Quality
- [ ] Add rate limiting for chat send.
- [ ] Add rate limiting for `@warpbot`.
- [ ] Escape/sanitize message content.
- [ ] Reject non-participant access.
- [ ] Reject kicked/removed participant access.
- [ ] Add audit log for moderation.
- [ ] Add tests for permission and moderation.
- [ ] Add tests for translation and WarpBot contracts.
- [ ] Add manual E2E/UAT checklist.

