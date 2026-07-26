# Hotfix: Open Windows file picker when downloading a document

Date: 2026-07-26
Reporter: User feedback

## Bug

When a user clicks Download on the workspace document detail page, the document is not presented through the Windows file picker/File Explorer on the user's machine.

## Root Cause

The primary Download action used a programmatic anchor while a separate Save As action used the native picker. This split behavior meant clicking Download did not open Windows File Explorer. The native picker must also open before the authenticated API request so the browser's user activation is still valid.

## Fix

Keep one Download action. Open the native File System Access picker before requesting the authenticated Blob, then write the response to the selected file. Fall back to the existing anchor download behavior when the picker is unavailable. Keep the backend download contract unchanged.

## Verification

- Add frontend regression tests verifying that Download opens the native picker before loading the Blob and retains the browser-download fallback.
- All 12 frontend Node tests pass.
- The Next.js production build passes and the frontend Docker image is recreated.
- The backend `DownloadDocument` test passes (1/1).
- Frontend health responds with HTTP 200.

## Regression Risk

Low. The change is limited to the document detail download interaction; the fallback preserves current browser download behavior and no backend/storage contract changes are required.
