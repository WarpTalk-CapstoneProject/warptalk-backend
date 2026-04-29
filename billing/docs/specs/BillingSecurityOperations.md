# Billing Security Operations

## Encryption at rest

Billing data contains subscription, quota, payment reference, and usage audit records. Production deployments must place PostgreSQL data volumes on encrypted storage managed by the hosting platform or a KMS-backed disk encryption feature. Application code must not store billing secrets in tables, logs, or payloads.

Minimum production evidence:
- `pgdata` or managed PostgreSQL storage is encrypted at rest.
- Database backups and snapshots inherit encryption.
- KMS key ownership and rotation are documented by the platform team.
- Billing connection strings and PayOS/payment credentials are supplied through secret management or protected environment injection.

## Worker-side quota enforcement

Gateway quota checks are advisory only. Any backend worker that spends billable AI, audio, translation, transcript, or assistant resources must call `POST /api/v1/billing/quota/deduct` before acknowledging the resource event as complete.

Required worker behavior:
- Use a stable `SessionId`/event id as the quota idempotency key and as the `sessionId` payload field.
- Retry only with the same idempotency key for the same resource event.
- Treat `InsufficientQuota`, `QuotaNotFound`, `ConcurrencyConflict`, and authorization failures as non-billable failures and stop downstream resource processing.
- Do not rely on client-side redirects, gateway pre-checks, or cached quota as final authorization to spend resources.
- Emit a correlation id and workspace id on every deduct request.

## Service-to-service TLS

Production compose enables gateway-to-billing HTTPS with a gateway client certificate and billing-side client certificate thumbprint validation. Certificates must be provisioned through secret management and mounted read-only.

Required production variables:
- `BILLING_TLS_CERT_DIR`, `BILLING_TLS_CERT_PATH`, `BILLING_TLS_CERT_PASSWORD`
- `GATEWAY_CLIENT_CERT_PATH`, `GATEWAY_CLIENT_CERT_PASSWORD`, `GATEWAY_CLIENT_CERT_THUMBPRINT`
- `BILLING_SERVER_CERT_THUMBPRINT`

## Payment callback boundary

Provider-specific replay windows, amount/currency matching, and payment state transition hardening belong to the real payment integration work. Until that work is complete, payment webhook handling must not be treated as fully production-certified.
