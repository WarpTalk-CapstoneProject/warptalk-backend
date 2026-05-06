using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Common;
using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.Application;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Billing service API controller for managing plans, subscriptions, credits, and transactions.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class BillingController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IBillingService _billingService;
    private readonly ILogger<BillingController> _logger;
    private readonly IWorkspaceValidationService _workspaceValidationService;
    private readonly IIdempotencyService _idempotencyService;

    public BillingController(
        IBillingService billingService,
        ILogger<BillingController> logger,
        IWorkspaceValidationService workspaceValidationService,
        IIdempotencyService idempotencyService)
    {
        _billingService = billingService;
        _logger = logger;
        _workspaceValidationService = workspaceValidationService;
        _idempotencyService = idempotencyService;
    }

    /// <summary>Get all available billing plans (public)</summary>
    /// <remarks>
    /// Returns list of all billing plans available for subscription.
    /// No authentication required.
    /// 
    /// **Response Examples:**
    /// - 200 OK: List of 4 plans (Free, Basic, Pro, Enterprise) with name, price, credits
    /// - 500 Service Unavailable: Database or service error
    /// </remarks>
    [HttpGet("plans")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<PlanDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
    {
        try
        {
            var result = await _billingService.GetPlansAsync(ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : BadRequest(new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching plans");
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Get current workspace credits balance</summary>
    /// <remarks>
    /// Retrieves current credit balance for an active subscription.
    /// Requires authorization (Bearer token) and workspace access.
    /// 
    /// **Status Codes:**
    /// - 200 OK: Returns current balance, end date, subscription status
    /// - 400 Bad Request: Invalid workspace ID format
    /// - 403 Forbidden: Unauthorized access to workspace
    /// - 404 Not Found: No active subscription found
    /// - 500 Service Unavailable: Database or service error
    /// </remarks>
    [HttpGet("workspaces/{workspaceId:guid}/credits")]
    [ProducesResponseType(typeof(WorkspaceCreditsDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    public async Task<IActionResult> GetWorkspaceCredits(Guid workspaceId, CancellationToken ct)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
            var result = await _billingService.GetWorkspaceCreditsAsync(workspaceId, ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : StatusCode(result.ErrorCode == BillingErrorCodes.SUBSCRIPTION_NOT_FOUND ? 404 : 400,
                    new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching workspace credits for {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Get active subscription for workspace</summary>
    /// <remarks>
    /// Retrieves currently active subscription details including plan info.
    /// Requires authorization and workspace access.
    /// 
    /// **Status Codes:**
    /// - 200 OK: Returns subscription with plan details
    /// - 400 Bad Request: Invalid workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: No active subscription exists
    /// - 500 Service Unavailable: Error
    /// </remarks>
    [HttpGet("workspaces/{workspaceId:guid}/subscriptions/active")]
    [ProducesResponseType(typeof(SubscriptionDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    public async Task<IActionResult> GetActiveSubscription(Guid workspaceId, CancellationToken ct)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
            var result = await _billingService.GetActiveSubscriptionAsync(workspaceId, ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : StatusCode(result.ErrorCode == BillingErrorCodes.SUBSCRIPTION_NOT_FOUND ? 404 : 400,
                    new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active subscription for {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Create a new subscription for workspace</summary>
    /// <remarks>
    /// Creates new subscription for a workspace under selected plan.
    /// Each workspace can have only ONE active subscription at a time.
    /// Returns 409 Conflict if workspace already has active subscription.
    /// 
    /// **Request Validation:**
    /// - PlanId must be valid GUID (non-empty)
    /// - WorkspaceId must be valid GUID
    /// 
    /// **Status Codes:**
    /// - 201 Created: Subscription created with initial credits
    /// - 400 Bad Request: Invalid plan ID or workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: Workspace or plan not found
    /// - 409 Conflict: Active subscription already exists
    /// - 500 Service Unavailable: Error
    /// </remarks>
    [HttpPost("workspaces/{workspaceId:guid}/subscriptions")]
    [ProducesResponseType(typeof(SubscriptionDto), 201)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 409)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateSubscription(Guid workspaceId, [FromBody] CreateSubscriptionRequest request, CancellationToken ct)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);

            if (!ModelState.IsValid)
                return BadRequest(new ApiErrorResponse(BillingErrorMessages.VALIDATION_FAILED, BillingErrorCodes.VALIDATION_FAILED));

            var result = await _billingService.CreateSubscriptionAsync(workspaceId, request.PlanId, ct);
            return result.IsSuccess
                ? CreatedAtAction(nameof(GetWorkspaceCredits), new { workspaceId }, result.Value)
                : StatusCode(result.ErrorCode == BillingErrorCodes.SUBSCRIPTION_ALREADY_ACTIVE ? 409 : 400,
                    new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Top-up credits for workspace</summary>
    /// <remarks>
    /// Adds credits to active subscription. Requires billing_admin role or workspace access.
    /// Supports idempotency via Idempotency-Key header to prevent duplicate charges.
    /// 
    /// **Request Validation:**
    /// - Amount must be > 0
    /// - Workspace must have active subscription
    /// 
    /// **Idempotency:**
    /// - Include Idempotency-Key header for idempotent requests
    /// - Duplicate request returns cached response (HTTP 200)
    /// 
    /// **Status Codes:**
    /// - 200 OK: Credits added, returns updated balance
    /// - 400 Bad Request: Invalid amount (≤ 0) or workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: No active subscription
    /// - 409 Conflict: Concurrency error, retry
    /// - 500 Service Unavailable: Error
    /// </remarks>
    [HttpPost("workspaces/{workspaceId:guid}/credits/topup")]
    [Authorize]
    [ProducesResponseType(typeof(WorkspaceCreditsDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 409)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    [Consumes("application/json")]
    public async Task<IActionResult> TopUpCredits(Guid workspaceId, [FromBody] TopUpCreditsRequest request, CancellationToken ct)
    {
        const string operation = "topup-credits";

        try
        {
            if (!User.IsInRole("billing_admin"))
            {
                await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
            }

            if (!ModelState.IsValid)
                return BadRequest(new ApiErrorResponse(BillingErrorMessages.VALIDATION_FAILED, BillingErrorCodes.VALIDATION_FAILED));

            var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
            var requestHash = PersistentIdempotencyService.HashPayload($"{operation}:{workspaceId}:{request.Amount}");

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var cached = await _idempotencyService.GetResponseJsonAsync(idempotencyKey, operation, requestHash, ct);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    var cachedDto = JsonSerializer.Deserialize<WorkspaceCreditsDto>(cached, JsonOptions);
                    return Ok(cachedDto);
                }
            }

            var result = await _billingService.TopUpCreditsAsync(workspaceId, request.Amount, "topup", null, ct);
            if (!result.IsSuccess)
                return StatusCode(result.ErrorCode == BillingErrorCodes.SUBSCRIPTION_NOT_FOUND ? 404 : 400,
                    new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));

            if (!string.IsNullOrWhiteSpace(idempotencyKey) && result.Value is not null)
            {
                await _idempotencyService.StoreResponseJsonAsync(idempotencyKey, operation, requestHash, JsonSerializer.Serialize(result.Value, JsonOptions), workspaceId, ct);
            }

            return Ok(result.Value);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ApiErrorResponse(ex.Message, BillingErrorCodes.CONCURRENCY_CONFLICT));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error topping up credits for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Consume credits for service usage</summary>
    /// <remarks>
    /// Deducts credits from active subscription for resource usage (STT, TTS, Translation, etc).
    /// Returns 402 Payment Required if insufficient credits.
    /// Supports idempotency via Idempotency-Key header.
    /// 
    /// **Request Validation:**
    /// - Amount must be > 0
    /// - ReferenceType must be non-empty (e.g., 'stt', 'tts', 'translation')
    /// - Workspace must have active subscription
    /// 
    /// **Idempotency:**
    /// - Include Idempotency-Key header for idempotent requests
    /// - Uses reference ID + type + amount to detect duplicate requests
    /// 
    /// **Status Codes:**
    /// - 200 OK: Credits consumed, returns updated balance
    /// - 400 Bad Request: Invalid amount/workspace/reference type
    /// - 402 Payment Required: Insufficient credits (balance &lt; amount)
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: No active subscription
    /// - 409 Conflict: Concurrency error, retry
    /// - 500 Service Unavailable: Error
    /// 
    /// **Business Rules:**
    /// - Cannot consume if balance - amount &lt; 0
    /// - Concurrent requests handled with optimistic locking
    /// </remarks>
    [HttpPost("workspaces/{workspaceId:guid}/credits/consume")]
    [ProducesResponseType(typeof(WorkspaceCreditsDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 402)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 409)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    [Consumes("application/json")]
    public async Task<IActionResult> ConsumeCredits(Guid workspaceId, [FromBody] ConsumeCreditsRequest request, CancellationToken ct)
    {
        const string operation = "consume-credits";

        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);

            if (!ModelState.IsValid)
                return BadRequest(new ApiErrorResponse(BillingErrorMessages.VALIDATION_FAILED, BillingErrorCodes.VALIDATION_FAILED));

            var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
            var requestHash = PersistentIdempotencyService.HashPayload($"{operation}:{workspaceId}:{request.Amount}:{request.ReferenceType}:{request.ReferenceId}");

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var cached = await _idempotencyService.GetResponseJsonAsync(idempotencyKey, operation, requestHash, ct);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    var cachedDto = JsonSerializer.Deserialize<WorkspaceCreditsDto>(cached, JsonOptions);
                    return Ok(cachedDto);
                }
            }

            var result = await _billingService.ConsumeCreditsAsync(workspaceId, request.Amount, request.ReferenceType, request.ReferenceId, ct);
            if (!result.IsSuccess)
                return StatusCode(result.ErrorCode switch
                {
                    BillingErrorCodes.INSUFFICIENT_CREDITS => 402,
                    BillingErrorCodes.SUBSCRIPTION_NOT_FOUND => 404,
                    _ => 400
                }, new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));

            if (!string.IsNullOrWhiteSpace(idempotencyKey) && result.Value is not null)
            {
                await _idempotencyService.StoreResponseJsonAsync(idempotencyKey, operation, requestHash, JsonSerializer.Serialize(result.Value, JsonOptions), workspaceId, ct);
            }

            return Ok(result.Value);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ApiErrorResponse(ex.Message, BillingErrorCodes.CONCURRENCY_CONFLICT));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming credits for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Get credit transaction history</summary>
    /// <remarks>
    /// Retrieves paginated list of credit transactions (topup, consume).
    /// Supports pagination with configurable page size.
    /// 
    /// **Query Parameters:**
    /// - pageNumber: 1-based page number (min: 1)
    /// - pageSize: Items per page, 1-200 (default: 50, clamped to max 200)
    /// 
    /// **Pagination Validation:**
    /// - Returns 400 if pageNumber &lt; 1
    /// - Returns 400 if pageSize &lt; 1 or pageSize &gt; 200
    /// 
    /// **Status Codes:**
    /// - 200 OK: Paginated credit transactions with metadata (totalCount, totalPages)
    /// - 400 Bad Request: Invalid pagination or workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: Workspace not found
    /// - 500 Service Unavailable: Error
    /// 
    /// **Response Structure:**
    /// ```json
    /// {
    ///   "items": [{ workspaceId, amount, type, referenceType, createdAt }, ...],
    ///   "pageNumber": 1,
    ///   "pageSize": 50,
    ///   "totalCount": 150,
    ///   "totalPages": 3
    /// }
    /// ```
    /// </remarks>
    [HttpGet("workspaces/{workspaceId:guid}/credits/history")]
    [ProducesResponseType(typeof(PaginatedResponse<CreditTransactionDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    public async Task<IActionResult> GetCreditHistory(Guid workspaceId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);

            var result = await _billingService.GetCreditHistoryAsync(workspaceId, pageNumber, pageSize, ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : BadRequest(new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit history for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Get payment transaction history</summary>
    /// <remarks>
    /// Retrieves paginated list of payment transactions (subscription payments).
    /// Supports pagination with configurable page size.
    /// 
    /// **Query Parameters:**
    /// - pageNumber: 1-based page number (min: 1)
    /// - pageSize: Items per page, 1-200 (default: 50)
    /// 
    /// **Pagination Validation:**
    /// - Returns 400 if pageNumber &lt; 1
    /// - Returns 400 if pageSize &lt; 1 or pageSize &gt; 200
    /// 
    /// **Status Codes:**
    /// - 200 OK: Paginated payment transactions
    /// - 400 Bad Request: Invalid pagination or workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: Workspace not found
    /// - 500 Service Unavailable: Error
    /// 
    /// **Response Structure:**
    /// ```json
    /// {
    ///   "items": [{ id, workspaceId, planId, status, amount, createdAt }, ...],
    ///   "pageNumber": 1,
    ///   "pageSize": 50,
    ///   "totalCount": 10,
    ///   "totalPages": 1
    /// }
    /// ```
    /// </remarks>
    [HttpGet("workspaces/{workspaceId:guid}/transactions")]
    [ProducesResponseType(typeof(PaginatedResponse<TransactionDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    public async Task<IActionResult> GetTransactionHistory(Guid workspaceId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);

            var result = await _billingService.GetTransactionHistoryAsync(workspaceId, pageNumber, pageSize, ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : BadRequest(new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transaction history for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }

    /// <summary>Cancel workspace subscription</summary>
    /// <remarks>
    /// Cancels active subscription. Must have active subscription to cancel.
    /// Returns 404 if no active subscription found.
    /// No response body returned (204 No Content on success).
    /// 
    /// **Request Validation:**
    /// - CancellationReason is optional string
    /// 
    /// **Status Codes:**
    /// - 204 No Content: Subscription cancelled successfully (no body)
    /// - 400 Bad Request: Invalid workspace ID
    /// - 403 Forbidden: Unauthorized access
    /// - 404 Not Found: No active subscription to cancel
    /// - 500 Service Unavailable: Error
    /// 
    /// **Business Rules:**
    /// - Remaining credits are forfeited (not refunded)
    /// - Workspace can create new subscription after cancellation
    /// </remarks>
    [HttpPost("workspaces/{workspaceId:guid}/subscriptions/cancel")]
    [ProducesResponseType(typeof(WorkspaceCreditsDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    [Produces("application/json")]
    [Consumes("application/json")]
    public async Task<IActionResult> CancelSubscription(Guid workspaceId, [FromBody] CancelSubscriptionRequest request, CancellationToken ct)
    {
        try
        {
            await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);

            var result = await _billingService.CancelSubscriptionAsync(workspaceId, request.CancellationReason, ct);
            return result.IsSuccess
                ? Ok(result.Value)
                : StatusCode(result.ErrorCode == BillingErrorCodes.SUBSCRIPTION_NOT_FOUND ? 404 : 400,
                    new ApiErrorResponse(result.Error ?? BillingErrorMessages.GetMessage(result.ErrorCode ?? string.Empty), result.ErrorCode ?? BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ApiErrorResponse(BillingErrorMessages.INVALID_WORKSPACE_ID, BillingErrorCodes.INVALID_WORKSPACE_ID));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, BillingErrorCodes.WORKSPACE_NOT_FOUND));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new ApiErrorResponse(ex.Message, BillingErrorCodes.VALIDATION_FAILED));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription for workspace {WorkspaceId}", workspaceId);
            return StatusCode(500, new ApiErrorResponse(BillingErrorMessages.GetMessage(BillingErrorCodes.SERVICE_UNAVAILABLE), BillingErrorCodes.SERVICE_UNAVAILABLE));
        }
    }
}
