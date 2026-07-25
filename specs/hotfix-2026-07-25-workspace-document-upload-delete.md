# Hotfix: workspace document upload and delete failures
Date: 2026-07-25
Reporter: User

## Bug
Workspace document upload can show a generic failure in the UI, deleted or missing documents surface as 400 Bad Request instead of the correct not-found/forbidden/server error, and security/embedding AI workers cannot connect to Redis with `No address associated with hostname`.

## Root Cause
The WorkspaceDocumentsController mapped most application failures to HTTP 400, masking `NOT_FOUND`, `FORBIDDEN`, and `INTERNAL_SERVER_ERROR`. The AI worker compose file used the Redis container name `warptalk-redis`; on the shared compose network, the stable service DNS alias is `redis`.

## Fix
Map workspace document API errors to their correct HTTP status codes and point AI worker `REDIS_URL` values at `redis:6379`.

## Verification
Run workspace service tests, including controller regression coverage for delete returning 404, 403, and 500. Validate AI compose config resolves Redis through the shared backend network.

## Regression Risk
Low. The API response body shape remains `ApiErrorResponse`; only HTTP status codes are corrected. AI workers must still be attached to `warptalk-infrastructure_warptalk-net`.
