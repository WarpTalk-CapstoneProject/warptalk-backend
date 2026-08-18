# WarpTalk Backend — Postman Collection

Full API surface of the backend microservices, organized into folders that mirror the
backend's own module layout: `auth`, `workspace`, `billing`, `meeting`, `translation-room`,
`transcript`, `notification`, `assistant`, plus a `Gateway / Platform` folder for health
checks and realtime hub references.

## Files

- `WarpTalk-Backend.postman_collection.json` — the collection (245 requests).
- `environments/WarpTalk-Backend.Local.postman_environment.json` — local-dev environment
  (`baseUrl` = `http://localhost:5200`, matching `gateway/src/WarpTalk.Gateway/Properties/launchSettings.json`).

## Import

1. Postman → **Import** → select both JSON files (or drag them in).
2. Select the **WarpTalk Backend - Local** environment in the top-right environment picker.

## Auth

1. Run `Auth > Account > Login`.
2. Copy `accessToken` (and `refreshToken`) from the response into the environment's
   `accessToken` / `refreshToken` variables.
3. Every other request inherits the collection's Bearer auth (`{{accessToken}}`) automatically,
   except the ones explicitly marked **Auth: none** in their description (registration, login,
   public plans, Stripe/LiveKit webhooks, invitation preview, health checks).

Requests with a role requirement (`SystemAdmin` policy, `AdminSystem`/`Admin` role, etc.) are
noted in their description — you need a token for a user holding that role/claim, a normal
Bearer token is not enough for those.

## Variables to fill in as you go

`workspaceId`, `userId`, `roomId` / `translationRoomId`, `planId`, `voiceId`, plus the
one-shot tokens (`invitationToken`, `verificationToken`, `resetToken`, `googleIdToken`) —
set these in the environment as you create real resources through the collection.

## Known gaps / things to verify before relying on this

- **`billing-policy` may not be reachable through the gateway.** The gateway's YARP route table
  only proxies `/api/v1/billing/**` — `/api/v1/billing-policy` is a different path segment and
  has no matching route as of this generation. Verify, or call the billing service directly for
  now.
- **`/api/v1/refunds/**` has a gateway route but no backend controller implements it** — not
  included as a real request here (no `RefundsController` exists).
- **The `payment/` module in the backend repo is an unimplemented stub** (no `.csproj`, not in
  the solution). All payment/checkout/subscription logic currently lives under `billing` — that's
  where the `Payments`, `Plans`, `Subscriptions`, `Invoices`, `Credits`, `Usages` folders live in
  this collection.
- **Request bodies are best-effort scaffolding**, inferred from the backend's DTO type names
  (each request's description names the DTO, e.g. `Request DTO: CreateWorkspaceRequest`) rather
  than read field-by-field from source. Check the actual DTO in the corresponding service before
  trusting exact field names/types.
- **SignalR hubs** (`/hubs/translation-room`, `/hubs/notification`, `/hubs/billing`) are included
  as plain GET placeholders for path documentation only — they need a WebSocket/SignalR client to
  actually exercise, not Postman's HTTP request runner.

## Regenerating

This collection was generated from a controller-by-controller sweep of the backend source
(one Postman request per controller action, cross-checked against the gateway's YARP route
table in `gateway/src/WarpTalk.Gateway/appsettings.json`) rather than hand-written, since the
backend has no committed OpenAPI/Swagger spec to import from. If the backend's controllers
change significantly, re-sweep and regenerate rather than hand-patching — see the generator
approach used to build the current file if you want to script the next pass.
