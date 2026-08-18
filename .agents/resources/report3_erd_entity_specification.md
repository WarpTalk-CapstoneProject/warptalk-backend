# Report 3 ERD Entity Specification

Source: `My Drive/Report3/Report3_Software Requirement Specification.docx`

Report section: `3.1.5 Entity Relationship Diagram`

Source table: `Table 6. Entity Descriptions`

Purpose: reusable backend-agent reference for the Report 3 ERD entities. This file mirrors the entity descriptions used in the SRS so agents can keep ERD, report, and implementation discussions aligned.

## Scope

- The ERD is a requirements-level model used in Report 3.
- Entities are grouped by bounded context using the context name in parentheses.
- This resource records the entity descriptions only; physical database columns, keys, migrations, and implementation relationships remain in backend/database resources.
- VectorDB evidence is separate from the relational ERD. When included in Report 3, it should be presented as a supplemental indexed-knowledge storage figure, not as a relational ERD table.

## Entity Descriptions

| # | Entity | Description |
|---:|---|---|
| 1 | Users (auth) | Login accounts; store user identity and authentication-related ownership links. |
| 2 | Roles (auth) | Platform-level RBAC roles assigned to users. |
| 3 | Permissions (auth) | Platform-level permissions granted through roles. |
| 4 | RefreshTokens (auth) | Refresh tokens linked to users for session renewal and token rotation. |
| 5 | UserSettings (auth) | Per-user preferences and settings. |
| 6 | VoiceConsents (voice) | Voice-clone consent records linked to users and translation rooms. |
| 7 | VoiceProfiles (voice) | User voice profiles used for voice-clone features. |
| 8 | VoiceSamples (voice) | Voice sample files or metadata used to build voice profiles. |
| 9 | Workspaces (workspace) | Tenant boundary; one workspace represents one organization. |
| 10 | WorkspaceMembers (workspace) | A user's membership inside a workspace. |
| 11 | WorkspaceInvitations (workspace) | Pending invitations to join a workspace. |
| 12 | WorkspaceDocuments (workspace) | Documents owned by or shared inside a workspace. |
| 13 | WorkspaceVerifiedDomains (workspace) | Corporate email domains verified for a workspace. |
| 14 | TranslationRooms (translation_room) | Business-layer translation rooms owned by workspaces and created by users. |
| 15 | TranslationRoomParticipants (translation_room) | Business-layer participants in a translation room. |
| 16 | TranslationRoomInvitations (translation_room) | Invitations to join a translation room. |
| 17 | TranslationRoomArtifacts (translation_room) | Exported or generated artifacts for a translation room. |
| 18 | TranslationRoomFeedbacks (translation_room) | Post-room feedback for translation rooms. |
| 19 | MeetingRooms (meeting) | Runtime/provider-layer meeting rooms spawned from translation rooms. |
| 20 | RtcStreamParticipants (meeting) | Runtime RTC participants representing translation-room participants. |
| 21 | MeetingTracks (meeting) | Audio/video tracks published by RTC participants. |
| 22 | RtcSessionRevocations (meeting) | Runtime session revocation records for RTC access. |
| 23 | MeetingChatMessages (meeting) | Chat messages sent inside meeting rooms. |
| 24 | MeetingChatTranslations (meeting) | Translated versions of meeting chat messages. |
| 25 | MeetingChatAssistantRequests (meeting) | Questions or requests sent to the meeting AI assistant. |
| 26 | Transcripts (transcript) | Transcript metadata for meeting or translation-room sessions. |
| 27 | TranscriptSegments (transcript) | Individual speech segments with speaker, text, and timing metadata. |
| 28 | TranscriptCorrections (transcript) | Corrections applied to transcript segments. |
| 29 | TranscriptExports (transcript) | Exported transcript documents. |
| 30 | TranslationContents (transcript) | Deduplicated translated text content. |
| 31 | SegmentTranslationLinks (transcript) | Links between transcript segments and translation contents. |
| 32 | AudioDubbings (transcript) | Synthesized audio outputs for translated segments. |
| 33 | Glossaries (transcript) | Workspace glossary collections used during translation. |
| 34 | GlossaryTerms (transcript) | Individual terminology entries inside a glossary. |
| 35 | GlobalGlossaryTerms (transcript) | Platform-wide glossary terms. |
| 36 | Plans (subscription) | Billing plans that subscriptions can subscribe to. |
| 37 | Subscriptions (subscription) | Workspace subscription and credit-wallet state. |
| 38 | CreditTransactions (subscription) | Credit charges, credits, refunds, and usage references. |
| 39 | UsageRecords (subscription) | Raw usage metering records. |
| 40 | UsageRateCards (subscription) | Pricing/rate records used to calculate usage charges. |
| 41 | Payments (subscription) | Payment records for subscriptions or credit purchases. |
| 42 | Invoices (subscription) | Generated invoices linked to payments. |
| 43 | NotificationMessages (notification) | Notifications delivered to users. |
| 44 | AssistantConversations (assistant) | AI assistant conversation records linked to users or workspaces. |
| 45 | AssistantMessages (assistant) | Messages inside an AI assistant conversation. |
| 46 | AssistantToolCalls (assistant) | Tool-call records produced during assistant processing. |

## Report Integration Notes

- Keep this table synchronized with `Table 6. Entity Descriptions` in Report 3.
- If a new figure is inserted after `Figure 7. Entity Relationship Diagram`, renumber later static figure captions and the List of Figures.
- Do not rename the SRS relational ERD to a vendor-specific vector database diagram.
