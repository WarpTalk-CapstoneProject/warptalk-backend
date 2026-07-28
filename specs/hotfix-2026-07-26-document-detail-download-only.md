# Hotfix: Document detail download-only view

Date: 2026-07-26
Reporter: User feedback

## Bug

The document detail page renders extracted document text as a human-facing preview. Formatting is lost for files such as DOCX, PDF, and XLSX, which can make the displayed content misleading.

## Root Cause

The frontend displays the backend's extracted text/table representation. That representation is intended for AI ingestion and does not preserve the source document's original layout.

## Fix

Remove the extracted-text viewer and editor from the document detail UI. Keep one original-file Download action that opens the native file picker when available, plus document metadata/status. Keep backend extraction and storage unchanged because AI indexing still depends on it.

## Verification

- Document detail page no longer requests or renders extracted text.
- A single Download action remains available; there is no separate Save As action.
- All 12 frontend Node tests pass.
- The Next.js production build passes and the frontend Docker image is recreated.

## Regression Risk

Low. Only the human-facing extracted-text viewer/editor is removed; document download, metadata, access policies, approval, and AI ingestion contracts remain unchanged.
