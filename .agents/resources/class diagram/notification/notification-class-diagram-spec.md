# Class Diagram Specification - Notification Module

Key classes of the Notification module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `NotificationMessage` | `Id, CreatedAt, UserId, Type, Title, Content, ActionUrl, PayloadJson, IsRead, ReadAt` | User notification entity partitioned by `created_at` (`_y2025`, `_y2026`, `_y2027`, `_default`); stores title, body payload, action URLs, and read status. |
| `NotificationPreference` | `Id, UserId, NotificationType, EmailEnabled, PushEnabled, InAppEnabled, UpdatedAt` | User preference entity controlling notification delivery channels (`Email`, `Push`, `InApp`) for specific notification types. |
| `NotificationTemplate` | `Id, Type, Channel, Subject, BodyTemplate, Variables, IsActive` | Reusable message template entity storing subject and body templates with placeholder variables. |
| `PushSubscription` | `Id, UserId, DeviceToken, Platform, DeviceName, IsActive, LastUsedAt` | Entity storing user device tokens for WebPush (VAPID) or FCM push notification dispatches. |
| `AdminNotification` | `Id, Title, Content, Type, Payload, TargetAudienceMode, TargetAudienceData, Status, CreatedBy` | Admin broadcast notification entity tracking system-wide announcement dispatches and target audience criteria. |
| `NotificationsController` | `GetNotifications(...), MarkAsRead(...), GetPreferences(...), UpdatePreferences(...)` | API controller exposing user notification inbox querying, read status updates, and channel preference management. |
| `AdminNotificationsController` | `CreateAdminNotification(...), BroadcastNotification(...)` | API controller allowing administrators to compose, preview, and send broadcast notifications. |
| `NotificationHub` | `JoinUserGroupAsync(...), SendRealtimeNotificationAsync(...)` | SignalR WebSocket hub delivering instant in-app alerts to connected users. |
| `NotificationService` | `SendNotificationAsync(...), MarkNotificationReadAsync(...), UpdatePreferencesAsync(...)` | Core application service evaluating user channel preferences, rendering template variables, and dispatching notifications across channels. |
| `AdminNotificationService` | `CreateBroadcastNotificationAsync(...), DispatchBroadcastAsync(...)` | Application service resolving target user audiences and managing bulk notification distribution. |
| `EmailTemplateRenderer` | `RenderTemplateAsync(...)` | Application component rendering email templates with dynamic model payloads. |
| `MailkitSmtpAdapter` | `SendEmailAsync(...)` | Infrastructure adapter executing email dispatches via Resend or SMTP gateways. |
| `WebPushGateway` | `SendPushPayload(...)` | External Web Push adapter delivering browser push notifications using WebPush VAPID protocols. |
| `NotificationDbContext` | `NotificationMessages, NotificationPreferences, NotificationTemplates, PushSubscriptions, AdminNotifications` | Entity Framework Core DbContext managing persistence for user notifications, channel preferences, templates, and push subscriptions. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity notification operations. |
| `NotificationMessageRepository` | `GetByUserIdAsync(...), AddAsync(...)` | Persistence repository for retrieving user notification inbox records and persisting new alerts. |
| `NotificationPreferenceRepository` | `GetByUserIdAsync(...)` | Persistence repository retrieving user channel preferences for specific notification categories. |
