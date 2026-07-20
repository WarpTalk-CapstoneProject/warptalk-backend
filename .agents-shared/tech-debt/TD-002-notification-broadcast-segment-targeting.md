# AdminNotification: Broadcast & Segment Targeting Not Implemented

**Date**: 2026-07-20
**Topic**: NotificationService — `AdminNotification` delivery for `Broadcast` and `Segment` target modes
**Context**: FE-07 is in scope (SRS Report 3, Business Rules BR-31–BR-33); this is a gap in an in-scope feature, not a scope exclusion.

## What's broken

`AdminNotificationService.CreateAdminNotificationAsync` (warptalk-backend/notification/src/WarpTalk.NotificationService.Application/Services/AdminNotificationService.cs) accepts 3 `TargetAudienceMode` values: `SpecificUsers`, `Broadcast`, `Segment`. It validates the request, saves the `AdminNotification` row, and publishes chunked `DeliveryEventPayload` events to the `admin-notifications-delivery` Redis Stream regardless of mode.

The consumer, `NotificationStreamConsumerService.ProcessChunkAsync` (warptalk-backend/notification/src/WarpTalk.NotificationService.API/HostedServices/NotificationStreamConsumerService.cs:101), only resolves target user IDs for `SpecificUsers`:

```csharp
else if (payload.TargetAudienceMode == NotificationConstants.TargetModeBroadcast)
{
    // For broadcast, ideally we query a User microservice to get all IDs.
    // For now, since this is a capstone, we assume the specific user list is passed or we leave it empty.
    _logger.LogWarning("Broadcast mode resolution is not fully implemented. Mocking empty list.");
}
else if (payload.TargetAudienceMode == NotificationConstants.TargetModeSegment)
{
    _logger.LogWarning("Segment mode resolution is not fully implemented. Mocking empty list.");
}
```

`targetUserIds` stays empty for both modes, so `if (!targetUserIds.Any()) return;` — no `NotificationMessage` rows are ever created, no realtime event is published. The admin notification silently never reaches anyone. Only `SpecificUsers` mode actually delivers today.

## Also missing (same feature area)

- `NotificationService.CreateNotificationAsync` does not check `NotificationPreference` before creating a notification (no path currently gates on it, despite `GetPreferences`/`UpdatePreferences` being fully implemented and user-editable).
- `PushSubscription` is a real, persisted entity but nothing in the notification-creation or admin-delivery paths reads it to actually send a push notification.

## What needs to happen

1. `Broadcast`: query Workspace/Auth service (gRPC) for all user IDs in scope (likely all members of the notification's owning workspace, or all users platform-wide — needs a decision) before chunking.
2. `Segment`: define what a "segment" actually means here (role-based? activity-based?) and implement the corresponding user-ID query.
3. Wire `NotificationPreference` into `CreateNotificationAsync` so a disabled preference actually suppresses creation/delivery.
4. Wire `PushSubscription` into delivery so an active subscription actually receives a push, not just an in-app row.

## Where this is documented

- Report 3 (SRS) BR-31–BR-33 describe the rules that exist today (preference defaults, page-size clamp, chunk size) — they describe current behavior, not the gap.
- Report 4 (SDD), section 3.7.2 Sequence Diagram, has a "Known gap" note pointing here.
