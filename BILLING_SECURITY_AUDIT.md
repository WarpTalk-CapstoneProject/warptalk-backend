# 🔒 Billing Service - Security Audit Report
**Date:** May 3, 2026 | **Framework:** .NET 9.0 (C#)

---

## 📋 ISO/IEC 27001-Style Security Checklist

### ✅ 1. ACCESS CONTROL (Authentication & Authorization)

| Item | Status | Details |
|------|--------|---------|
| JWT Authentication | ✅ Implemented | JwtBearer auth configured in Program.cs with token validation |
| Token Validation | ✅ Implemented | Validates Issuer, Audience, SigningKey, Lifetime (ClockSkew: 1 min) |
| Environment-Based Auth | ✅ Implemented | `Security:RequireAuthentication` flag enables/disables auth |
| RBAC/Ownership Checks | ⚠️ Partial | `WorkspaceAuthorizationHelper` validates workspace_id claims but needs [Authorize] attributes |
| Admin Endpoint Protection | ❌ Missing | `/api/admin/billing/*` endpoints lack `[Authorize]` attribute - **CRITICAL** |
| Webhook Protection | ⚠️ Partial | PayOS webhook has `[AllowAnonymous]` (intentional) but signature validation needed |
| Least Privilege | ⚠️ Partial | No granular role-based checks (admin, owner, user roles) |

**Issues Found:**
```csharp
// ❌ VULNERABLE: No [Authorize] on admin endpoints
[HttpPost("subscription/create")]  // Should be [Authorize(Roles = "admin")]
public async Task<IActionResult> CreateSubscription(...)

// ✅ CORRECT: Workspace access validation present
[HttpPost("create-link")]
public async Task<IActionResult> CreatePaymentLink(
    [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
    [FromBody] CreatePaymentLinkRequest request,
    CancellationToken ct)
{
    var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
    if (accessError != null) return accessError;
    // ...
}
```

---

### 🔐 2. SECRETS MANAGEMENT

| Item | Status | Details |
|------|--------|---------|
| Secrets in Code | ❌ Found | appsettings.Development.json contains hardcoded credentials |
| Environment Variables | ✅ Correct | Production config uses env vars (Jwt__Secret, POSTGRES_PASSWORD, PayOS keys) |
| Connection String Security | ⚠️ Risky | Development has plain "postgres:postgres" in JSON |
| PayOS Credentials | ✅ Correct | Set via `${PayOS_CHECKSUM_KEY:-}` environment variable |
| Swagger Secrets | ✅ OK | Swagger disabled in Production mode |

**Secrets Found:**
```json
// ❌ DEVELOPMENT ONLY - REMOVE BEFORE PRODUCTION
"ConnectionStrings": {
  "BillingDb": "Host=localhost;Port=55433;Database=warptalk;Username=postgres;Password=<REDACTED_DB_PASSWORD>;..."
}

// ✅ CORRECT - Production config
"Jwt:Secret": "${JWT_SECRET:?required}",
"PayOS:ChecksumKey": "${PAYOS_CHECKSUM_KEY:-}"
```

**Remediation:**
- ✅ Move dev credentials to appsettings.Development.json (gitignored)
- ✅ Ensure production appsettings.json contains NO secrets
- ✅ All secrets loaded from environment variables only

---

### 🛡️ 3. DATA PROTECTION (PII & Payment Data)

| Item | Status | Details |
|------|--------|---------|
| Payment Data Logging | ⚠️ Unchecked | No explicit PII masking in logs |
| Sensitive Fields | ⚠️ Review Needed | PayOsTransactionId, OrderCode, AmountVnd logged plaintext |
| HTTPS/TLS | ✅ Configured | `app.UseHttpsRedirection()` enabled in Program.cs |
| At-Rest Encryption | ❓ N/A | PostgreSQL pgcrypto extension status unknown |
| Column-Level Encryption | ❌ Not Implemented | No encryption for PaymentData columns |
| Data Minimization | ✅ Good | Only essential payment fields stored |

**Logging Concerns:**
```csharp
// ⚠️ RISK: PayOS transaction details logged without masking
await _paymentService.ProcessPayOsWebhookAsync(payload, ct);
// payload contains sensitive payment data

// ✅ BETTER: Implement sensitive data masking
_logger.LogInformation("Transaction processed: OrderCode={OrderCode}**, Amount={Amount}***",
    MaskSensitiveData(orderCode),
    MaskSensitiveData(amount));
```

---

### ✔️ 4. INPUT VALIDATION

| Item | Status | Details |
|------|--------|---------|
| DTO Validation | ❌ Missing | No FluentValidation or Data Annotations found |
| Guid Validation | ✅ Basic | WorkspaceAuthorizationHelper checks for `Guid.Empty` |
| Pagination Bounds | ⚠️ Partial | page, pageSize params accepted but no max size limit |
| Amount Validation | ❌ Missing | No range checks on decimal amounts |
| Enum Validation | ✅ Framework | .NET handles enum string conversion |
| SQL Injection | ✅ Protected | EF Core parameterized queries used |
| State Transition Validation | ⚠️ Unchecked | Subscription status changes not validated |

**Missing Validation:**
```csharp
// ❌ NO VALIDATION - Should validate:
[HttpGet("transactions/{workspaceId}")]
public async Task<IActionResult> GetWorkspaceTransactions(
    Guid workspaceId,
    [FromQuery] int page = 1,          // No max bounds
    [FromQuery] int pageSize = 20,     // No max size (could be 999999)
    CancellationToken ct = default)

// ✅ RECOMMENDED: Add validation
if (pageSize > 100) pageSize = 100;
if (page < 1) page = 1;
if (workspaceId == Guid.Empty) 
    return BadRequest("Invalid workspaceId");
```

---

### 📊 5. LOGGING & AUDIT

| Item | Status | Details |
|------|--------|---------|
| Structured Logging | ✅ Implemented | Serilog with context enrichment configured |
| Correlation IDs | ✅ Available | `HttpContext.TraceIdentifier` used in exception middleware |
| Sensitive Data Masking | ❌ Missing | No redaction of payment data in logs |
| Audit Trail | ⚠️ Partial | QuotaAuditLogs table exists but audit events not consistently logged |
| Exception Handling | ✅ Implemented | ExceptionHandlingMiddleware catches & logs with TraceId |
| Log Levels | ✅ Configured | Development: Information, Microsoft.AspNetCore: Warning |

**Logging Best Practice:**
```csharp
// ✅ CORRECT: Include TraceId
_logger.LogError(ex,
    "Unhandled exception. TraceId={TraceId}, Path={Path}",
    context.TraceIdentifier,
    context.Request.Path);

// ❌ TODO: Mask sensitive data
_logger.LogInformation("Transaction: Id={TransactionId}, Amount={Amount}, PayOsId={PayOsId}",
    transaction.Id,
    transaction.AmountVnd,  // ⚠️ Should mask last digits
    transaction.PayOsTransactionId);  // ⚠️ Should mask
```

---

### 🏥 6. AVAILABILITY & RESILIENCE

| Item | Status | Details |
|------|--------|---------|
| Timeout Configuration | ⚠️ Default | No explicit timeout on database/PayOS calls |
| Retry Logic | ❌ Missing | No retry on transient PayOS failures |
| Health Checks | ✅ Implemented | `/api/health`, `/api/health/db`, `/api/health/live`, `/api/health/ready` |
| Rate Limiting | ❌ Missing | No rate limiting on webhook endpoints |
| Circuit Breaker | ❌ Missing | No Polly circuit breaker for external APIs |
| Bounded Resources | ✅ Partial | pageSize defaults to 20, but no enforcement max |
| Fallback Behavior | ❌ Missing | PayOS mock service configured but no fallback logic |

**Missing Resilience:**
```csharp
// ❌ NO TIMEOUT/RETRY
await _paymentService.ProcessPayOsWebhookAsync(payload, ct);

// ✅ RECOMMENDED: Add Polly circuit breaker
var policy = Policy
    .Handle<HttpRequestException>()
    .Or<TimeoutRejectedException>()
    .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 3,
        durationOfBreak: TimeSpan.FromSeconds(30));
```

---

### 📦 7. DEPENDENCIES & VERSIONS

| Package | Version | Status | Notes |
|---------|---------|--------|-------|
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.0 | ✅ Latest | Current stable |
| Microsoft.EntityFrameworkCore | 9.0.0 | ✅ Latest | No vulnerabilities known |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.0 | ✅ Latest | Compatible with EF Core 9.0 |
| Serilog.AspNetCore | 10.0.0 | ✅ Latest | No known vulnerabilities |
| Swashbuckle.AspNetCore | 10.1.7 | ✅ Latest | Recent patch |

**Justification Check:**
- ✅ All packages justified
- ✅ No outdated/deprecated packages found
- ✅ No unused dependencies detected

---

### 🚀 8. DEPLOYMENT & CONFIGURATION

| Item | Status | Details |
|------|--------|---------|
| CORS Policy | ✅ Configured | Allows origin list (dev: localhost:3000/5173, prod: empty) |
| CORS Preflight | ✅ Standard | Handled by ASP.NET Core default behavior |
| JWT Token Validation | ✅ Strict | Validates Issuer, Audience, Signing Key, Lifetime |
| TLS/HTTPS | ✅ Enforced | `app.UseHttpsRedirection()` in production |
| Environment Separation | ✅ Implemented | Development vs Production configs differ |
| Production Defaults | ⚠️ Review | See below |
| Secrets in Env Vars | ✅ Correct | PayOS & JWT secrets from environment |
| Database String from Env | ✅ Correct | Via docker-compose environment variables |

**Production Configuration Review:**
```json
// appsettings.json (Production)
{
  "Security": {
    "RequireAuthentication": true,           // ✅ GOOD
    "AllowInsecureWebhookSignatureInDevelopment": false,  // ✅ GOOD
    "RequireServiceClientCertificate": false  // ⚠️ Consider enabling
  },
  "Cors": {
    "AllowedOrigins": []                     // ✅ GOOD: No open CORS
  },
  "Billing": {
    "AutoSeedOnStartup": false               // ✅ GOOD: No auto-seed in prod
  }
}
```

---

## 📈 Security Score Summary

```
Access Control:        60% ⚠️  (Missing [Authorize] attributes on admin endpoints)
Secrets Management:    80% ⚠️  (Hardcoded dev creds in Development.json)
Data Protection:       70% ⚠️  (No PII masking in logs, no column encryption)
Input Validation:      40% ❌  (No FluentValidation, missing bounds checks)
Logging & Audit:       75% ⚠️  (Good structure, missing sensitive data masking)
Availability:          50% ❌  (No timeouts, retries, or rate limiting)
Dependencies:          95% ✅  (All up-to-date)
Deployment:            85% ✅  (Good config separation)

OVERALL: 68% - REQUIRES IMPROVEMENTS ⚠️
```

---

## 🔴 CRITICAL ISSUES (Fix Immediately)

### 1. Missing [Authorize] on Admin Endpoints
```csharp
// ❌ VULNERABLE
[HttpPost("subscription/create")]
public async Task<IActionResult> CreateSubscription(...)

// ✅ FIX
[Authorize(Roles = "admin,owner")]
[HttpPost("subscription/create")]
public async Task<IActionResult> CreateSubscription(...)
```

### 2. No Input Validation
```csharp
// ✅ ADD FluentValidation
public class CreateSubscriptionCommandValidator : AbstractValidator<CreateSubscriptionCommand>
{
    public CreateSubscriptionCommandValidator()
    {
        RuleFor(x => x.WorkspaceId).NotEmpty();
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}
```

### 3. Missing Pagination Limits
```csharp
// ✅ FIX
const int MAX_PAGE_SIZE = 100;
if (pageSize > MAX_PAGE_SIZE) pageSize = MAX_PAGE_SIZE;
if (page < 1) page = 1;
```

---

## 🟡 HIGH PRIORITY (Fix Soon)

1. **Add sensitive data masking in logs**
2. **Implement rate limiting on webhooks** (300 req/hour)
3. **Add timeout configuration** (30s default)
4. **Implement circuit breaker** for PayOS integration
5. **Remove hardcoded credentials** from Development.json
6. **Add webhook signature validation** (HMAC-SHA256)

---

## 🟢 RECOMMENDATIONS (Best Practices)

1. ✅ Implement request/response logging middleware
2. ✅ Add API key rotation policy
3. ✅ Set up security headers (HSTS, X-Frame-Options, etc.)
4. ✅ Implement API versioning (v1 already present)
5. ✅ Add OpenTelemetry tracing
6. ✅ Set up SIEM integration for audit logs
7. ✅ Conduct regular penetration testing

---

## 📝 Next Steps

Priority Order:
1. Add [Authorize] attributes to all admin endpoints
2. Implement input validation with FluentValidation
3. Add pagination bounds enforcement
4. Implement sensitive data masking in logs
5. Add rate limiting middleware
6. Configure timeouts and circuit breakers

---

**Report Generated:** 2026-05-03  
**Framework:** .NET 9.0 | **Database:** PostgreSQL 16 | **Auth:** JWT Bearer  
**Status:** ⚠️ **PARTIALLY COMPLIANT** - Requires Security Hardening
