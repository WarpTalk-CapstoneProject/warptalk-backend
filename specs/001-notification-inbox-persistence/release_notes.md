# WT-7 Notification Inbox Persistence - Release Notes

## Environment Notes
- **Database**: Entity Framework Core migrations have been applied to `NotificationDbContext`. Ensure `dotnet ef database update` is run on the target environment during deployment to create the new `NotificationMessages` table.
- **Variables**: No new environment variables are strictly required, but ensure that the connection strings to PostgreSQL and Redis are correct.
- **Dependencies**: No new external infrastructure dependencies; uses the existing PostgreSQL database and Redis cluster.

## Rollback / Fallback Notes
- **Rollback Plan**: If issues arise with notification persistence in production (e.g. high DB latency or crashes):
  - Database schema: `dotnet ef database update <PreviousMigrationName>` can be used to roll back the schema.
  - Code rollback: Revert the branch `feat/wt-7-bo-sung-module-hop-thu-thong-bao-notification-inbox`.
  - Fallback: Realtime notifications via SignalR might fail if the database insertion is tightly coupled. If an emergency fallback is needed, we would need to temporarily revert `NotificationGrpcServiceImpl` to not rely on DB persistence.

## Known Limitations
- **Pagination**: Currently uses offset-based pagination (`page`, `pageSize`) which could degrade in performance if a user has thousands of notifications. Cursor-based pagination is documented as tech debt for the future.
- **Data Retention**: No background job cleans up old notifications yet. If scale becomes an issue, a retention policy (e.g., auto-delete after 30 days) and corresponding cron job will need to be implemented (see Tech Debt).
- **Payload Schema Validation**: Currently missing strict allowlist validation for `PayloadJson`. As a mitigation, XSS checks and basic validations are in place, but deeper JSON schema validation is deferred to a follow-up task.

## Demo Evidence
- **Postman Tests**: All tests in `04 - Notification via Gateway` passed successfully.
  - Paging bounds check: successfully returns 200/400 and caps `pageSize` at 100.
  - IDOR check: Users cannot mark other users' notifications as read (returns 403/404).
  - Validation: Ensure no raw HTML is returned in `PayloadJson` to prevent XSS.
  - Happy Path: Pagination and Mark As Read works as expected, verified in Postman collections.
