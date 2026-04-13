# Refactor: Rename Meeting to TranslationRoom
Date: 2026-04-14

## What is being refactored
We are renaming all occurrences of `Meeting` to `TranslationRoom` across all layers of the WarpTalk application. This includes:
- Models / Entities (`Meeting.cs` -> `TranslationRoom.cs`)
- Application logic and controllers (`MeetingController` -> `TranslationRoomController`)
- gRPC services (`meeting.proto` -> `translation_room.proto`)
- Frontend variables and API requests (`warptalk-web`, `warptalk-desktop`)
- Database schemas and initialization scripts (`meeting_svc` -> `translation_room_svc`)

## Why
According to user feedback, WarpTalk does not "create" meetings; it creates a "Translation Session" or "TranslationRoom" that runs parallel to Google Meet. Changing "meeting" to "TranslationRoom" clarifies the app's domain and reduces confusion.

## What does NOT change
Core translation logic, AI model flows, and signal generation mechanics remain the same. The change primarily addresses nomenclature, directory structures, and API paths.

## Constitution compliance check
- [x] Still follows Article I (Clean Architecture)? Yes.
- [x] Communication channels unchanged (Article II)? Yes.
- [x] Tests still pass? Will be verified.
