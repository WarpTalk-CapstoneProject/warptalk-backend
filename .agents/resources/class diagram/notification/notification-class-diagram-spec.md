# Class Diagram Specification - Notification Module

Key classes of the Notification module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `NotificationMessage` | `Id, UserId, WorkspaceId, Title, Body, IsRead` | Individual notification record generated for a user; tracks title, payload body, and read/unread status. |
| `NotificationPreference` | `Id, UserId, Channel, Category` | User-defined notification channel preferences (Email, Web Push, In-App) per notification category. |
| `NotificationTemplate` | `Id, Code, SubjectTemplate, BodyTemplate` | Reusable email and push notification templates with locale and parameter placeholders. |
| `NotificationController` | `getNotifications(...), markAsRead(...), updatePreferences(...)` | Boundary controller exposing user endpoints for reading notifications and updating channel preferences. |
| `AdminNotificationController` | `createAdminNotification(...), broadcastNotification(...)` | Controller allowing system administrators to compose and send system-wide announcements. |
| `NotificationHub` | `joinUserGroup(...), sendRealtimeNotification(...)` | SignalR WebSocket hub delivering instant in-app alerts and notifications to connected users. |
| `NotificationService` | `sendNotificationAsync(...), markNotificationReadAsync(...), updatePreferencesAsync(...)` | Core service evaluating user preferences, rendering template placeholders, and dispatching notifications across channels. |
| `AdminNotificationService` | `createBroadcastNotificationAsync(...), dispatchBroadcastAsync(...)` | Service managing bulk message distribution to all active users or workspace members. |
| `EmailTemplateRenderer` | `renderTemplateAsync(...)` | Application component rendering Liquid/Razor email templates with dynamic model payloads. |
| `MailkitSmtpAdapter` | `sendEmailAsync(...)` | Infrastructure adapter sending email messages via Resend or SMTP gateways. |
| `WebPushGateway` | `sendPushPayload(...)` | External web push service delivering browser notifications using WebPush VAPID protocols. |
