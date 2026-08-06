# Class Diagram Specification - Billing Module

Key classes of the Billing module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `WorkspaceWallet` | `WalletId, WorkspaceId, Balance, Suspended` | Central credit balance entity for a workspace; tracks credit availability and handles automatic service suspension when cạn điểm (`Suspended = true`). |
| `CreditTransaction` | `TransactionId, WorkspaceId, Amount, Type` | Audit trail record for all credit movements, including Stripe top-ups (positive) and translation room usage deductions (negative). |
| `Subscription` | `SubscriptionId, WorkspaceId, Plan` | Active tier subscription (Free, Pro, Enterprise) determining monthly credit allocations and feature entitlements. |
| `UsageRecord` | `UsageId, RoomId, DurationSeconds` | Granular log of live meeting duration and AI translation speech processing seconds. |
| `PaymentsController` | `createCheckoutSession(...), handleStripeWebhook(...)` | Boundary controller handling Stripe checkout session creation and processing asynchronous payment webhooks. |
| `BillingServiceGrpc` | `consumeCredits(...), getBalance(...)` | gRPC service providing low-latency credit verification and deduction endpoints for live room workers. |
| `CreditsExhaustedConsumer` | `consumeCreditsExhausted(...)` | Asynchronous queue consumer handling wallet depletion events to trigger workspace room suspensions. |
| `CreditService` | `consumeCreditsAsync(...), topUpCreditsAsync(...), suspendOnExhaustion(...)` | Core service evaluating credit balances, processing top-ups, and locking room features on zero balance. |
| `UsageService` | `calculateCreditCost(...), recordUsage(...)` | Application service converting speech translation seconds into credit cost units based on active pricing models. |
| `StripeBillingGateway` | `createCheckoutSession(...), verifyWebhook(...)` | Integration gateway validating Stripe webhook cryptographic signatures and creating payment sessions. |
| `FractionalBillingWorker` | `consumeSpeechUsageTick(...), billPartialDuration(...)` | Background worker processing fractional usage ticks (second-by-second) to ensure precise heartbeat billing. |
| `StripePaymentGateway` | `checkoutSession(...), webhookEvents(...)` | External payment processing platform executing credit card top-ups and publishing payment status events. |
