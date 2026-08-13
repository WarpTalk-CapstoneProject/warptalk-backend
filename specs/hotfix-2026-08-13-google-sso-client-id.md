# Hotfix: Google SSO Client ID Contract
Date: 2026-08-13
Reporter: WT-361 / production login report

## Bug

Production Google SSO from `https://app.warptalk.io.vn/login?callbackUrl=%2Fworkspace`
reaches `POST /api/v1/auth/google-login` but returns `400 Bad Request`.

## Root Cause

The most likely production cause is Google OAuth client ID drift: the web bundle can be
built with a different `NEXT_PUBLIC_GOOGLE_CLIENT_ID` than the auth service runtime
`Authentication__Google__ClientId`. The existing frontend also sends an OAuth access
token under the field name `idToken`, forcing the backend onto its transitional
tokeninfo fallback where `aud` or `azp` mismatch returns `Invalid Google token`.

## Fix

Keep the backend audience validation intact, make missing or placeholder Google client
ID configuration fail closed with a clear startup/DI error, and pair it with a web
client fix that sends a real Google ID token.

## Verification

- Auth service Google token verifier tests cover foreign-client token rejection.
- New verifier test rejects empty, whitespace, and placeholder Google client IDs.
- Production deploy checks ensure the same Google client ID is wired into web build
  args and auth runtime env.

## Regression Risk

If local development intentionally relies on an empty Google client ID, Google SSO will
now fail fast instead of sending a misleading `Invalid Google token` response. Email and
password login are unaffected.
