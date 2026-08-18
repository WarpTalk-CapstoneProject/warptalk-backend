# Class Diagram Specification - Billing Module

Key classes of the Billing module are described in the Class Specification table below.

| Class | Field / Method | Description |
| :--- | :--- | :--- |
| `Plan` | `Id, Name, Slug, Tier, Price, Currency, BillingCycle, CreditsPerCycle, MaxParticipants, MaxLanguages, MaxActiveRooms, VoiceCloneEnabled, AiAssistantEnabled` | Entity defining system subscription tiers (e.g. Free, Pro, Enterprise), monthly pricing, credit quotas, and feature capabilities. |
| `Subscription` | `Id, UserId, WorkspaceId, PlanId, Status, CreditsRemaining, CreditsUsedThisCycle, CurrentPeriodStart, CurrentPeriodEnd, ServiceState, SuspendedReason` | Active subscription entity assigned to a user or workspace; tracks credit usage, billing periods, and service states (`healthy`, `low_balance`, `in_overage`, `suspended`). |
| `Payment` | `Id, SubscriptionId, UserId, Amount, TaxAmount, TotalAmount, Currency, PaymentMethod, Provider, ProviderTransactionId, Status` | Transaction entity recording credit card or external provider payments (e.g. Stripe, VNPay) and refund states. |
| `Invoice` | `Id, PaymentId, UserId, WorkspaceId, InvoiceNumber, StripeInvoiceId, Amount, Subtotal, Tax, Total, Status, PdfUrl` | Invoice document entity linked to a payment transaction; stores billing amounts, line items, and PDF links. |
| `CreditTransaction` | `Id, SubscriptionId, UserId, WorkspaceId, Amount, Type, ChargeType, Description, ReferenceId, BalanceAfter, PricingRateCardId, UsageRecordId` | Audit ledger entry capturing every credit top-up (+) or room translation consumption (-); links to rate cards and usage records. |
| `CreditBalanceSnapshot` | `Id, SubscriptionId, CreditsRemaining, CreditsUsedThisCycle, SnapshotAt` | Historical snapshot entity recording subscription credit balances at specific timestamps. |
| `UsageRecord` | `Id, SubscriptionId, UserId, WorkspaceId, TranslationRoomId, SegmentId, UsageType, Unit, Quantity, CreditsConsumed, DurationSeconds` | Detailed usage log recording speech translation seconds, TTS voice cloning ticks, and consumed credit units per room session. |
| `UsageRateCard` | `Id, ChargeType, Unit, Provider, Model, SourceLanguageCode, TargetLanguageCode, UnitPrice, Currency, EffectiveFrom, EffectiveTo` | Pricing rate card entity defining unit credit costs for specific AI models, providers, and language pairs. |
| `WorkspaceEntitlementOverride` | `WorkspaceId, EntitlementKey, Value, SetBy, UpdatedAt` | Override entity allowing custom feature entitlement values for specific workspaces (e.g. Enterprise contracts). |
| `SalesInquiry` | `Id, FirstName, LastName, WorkEmail, Company, RequestType, FeatureInterests, TargetLanguages, Status, WorkspaceId, SubscriptionId` | Entity capturing sales inquiry submissions and enterprise pricing lead conversions. |
| `SubscriptionsController` | `GetCurrentSubscription(...), UpgradePlan(...), CancelSubscription(...)` | API controller managing active subscriptions, plan upgrades, and cancellations. |
| `PaymentsController` | `CreateCheckoutSession(...), HandleStripeWebhook(...)` | API controller handling Stripe checkout session creation and processing asynchronous payment webhooks. |
| `InvoicesController` | `GetInvoices(...), DownloadInvoicePdf(...)` | API controller for querying user/workspace invoices and downloading PDF receipts. |
| `BillingServiceGrpc` | `ConsumeCredits(...), GetBalance(...)` | High-performance gRPC service providing low-latency credit verification and deduction endpoints for live room workers. |
| `SubscriptionService` | `CreateSubscriptionAsync(...), UpgradePlanAsync(...), EvaluateServiceStateAsync(...)` | Application service managing subscription lifecycles, renewal cycles, and service suspension evaluations. |
| `CreditService` | `ConsumeCreditsAsync(...), TopUpCreditsAsync(...), ProcessReversalAsync(...)` | Application service evaluating wallet credit balances, executing transactions, and processing credit reversals. |
| `UsageService` | `CalculateCreditCost(...), RecordUsage(...)` | Application service converting speech translation seconds into credit cost units based on active rate cards. |
| `StripeBillingGateway` | `CreateCheckoutSessionAsync(...), VerifyWebhookSignature(...)` | Integration gateway validating Stripe webhook signatures and generating Stripe checkout links. |
| `FractionalBillingWorker` | `ConsumeSpeechUsageTick(...), BillPartialDuration(...)` | Background worker processing second-by-second fractional usage ticks to ensure accurate heartbeat billing. |
| `BillingDbContext` | `Plans, Subscriptions, Payments, CreditTransactions, UsageRecords` | Entity Framework Core DbContext managing persistence for subscription plans, active subscriptions, payments, credit ledgers, and usage records. |
| `UnitOfWork` | `SaveChangesAsync(), BeginTransactionAsync(), CommitTransactionAsync()` | Manages transactional consistency for multi-entity billing operations. |
| `SubscriptionRepository` | `GetByWorkspaceIdAsync(...), AddAsync(...), Update(...)` | Persistence repository for retrieving and updating workspace subscription aggregate roots. |
| `CreditTransactionRepository` | `GetBySubscriptionIdAsync(...), AddAsync(...)` | Persistence repository managing credit ledger entries and balance adjustments. |
| `StripeApi` | `CreateCheckoutSessionAsync(...), VerifyWebhook(...)` | External Stripe API platform processing credit card checkout sessions and webhook events. |
