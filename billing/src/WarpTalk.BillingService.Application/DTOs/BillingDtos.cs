using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

// ============================================================================
// RESPONSE DTOs
// ============================================================================

public record SubscriptionDto
{
    public SubscriptionDto(
        Guid id,
        Guid? userId,
        Guid? workspaceId,
        Guid planId,
        string planName,
        decimal price,
        string status,
        int creditsRemaining,
        int creditsUsedThisCycle,
        DateTime currentPeriodStart,
        DateTime currentPeriodEnd,
        bool autoRenew,
        bool cancelAtPeriodEnd,
        DateTime createdAt,
        DateTime? cancelledAt)
    {
        Id = id;
        UserId = userId;
        WorkspaceId = workspaceId;
        PlanId = planId;
        PlanName = planName;
        Price = price;
        Status = status;
        CreditsRemaining = creditsRemaining;
        CreditsUsedThisCycle = creditsUsedThisCycle;
        CurrentPeriodStart = currentPeriodStart;
        CurrentPeriodEnd = currentPeriodEnd;
        AutoRenew = autoRenew;
        CancelAtPeriodEnd = cancelAtPeriodEnd;
        CreatedAt = createdAt;
        CancelledAt = cancelledAt;
    }

    public SubscriptionDto(
        Guid id,
        Guid workspaceId,
        Guid planId,
        string status,
        int currentCredits,
        DateTime startDate,
        DateTime? endDate,
        DateTime createdAt)
        : this(id, null, workspaceId, planId, string.Empty, 0m, status, currentCredits, 0, startDate, endDate ?? startDate, true, false, createdAt, null)
    {
    }

    public Guid Id { get; init; }
    public Guid? UserId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid PlanId { get; init; }
    public string PlanName { get; init; }
    public decimal Price { get; init; }
    public string Status { get; init; }
    public int CreditsRemaining { get; init; }
    public int CreditsUsedThisCycle { get; init; }
    public DateTime CurrentPeriodStart { get; init; }
    public DateTime CurrentPeriodEnd { get; init; }
    public bool AutoRenew { get; init; }
    public bool CancelAtPeriodEnd { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? WorkspaceName { get; init; }

    public int CurrentCredits => CreditsRemaining;
    public DateTime StartDate => CurrentPeriodStart;
    public DateTime? EndDate => CurrentPeriodEnd;
}

public record WorkspaceCreditsDto(
    Guid WorkspaceId,
    int CurrentCredits,
    DateTime? SubscriptionEndDate,
    string SubscriptionStatus = "active");

public record TransactionDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? SubscriptionId,
    decimal Amount,
    string Status,
    string? ExternalId,
    DateTime CreatedAt);

// ============================================================================
// REQUEST DTOs (with validation)
// ============================================================================

public record CreateSubscriptionRequest(
    [Required(ErrorMessage = "Plan ID is required")]
    Guid PlanId);

public record TopUpCreditsRequest(
    [Required(ErrorMessage = "Amount is required")]
    [Range(1, int.MaxValue, ErrorMessage = "Amount must be greater than 0")]
    int Amount);

public record CancelSubscriptionRequest(
    string? CancellationReason = null);

// ============================================================================
// PAGINATION
// ============================================================================

public record PaginationParams(
    [Range(1, 200, ErrorMessage = "Page size must be between 1 and 200")]
    int PageSize = 50,
    
    [Range(1, int.MaxValue, ErrorMessage = "Page number must be >= 1")]
    int PageNumber = 1);

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;
}

// ============================================================================
// ERROR RESPONSE
// ============================================================================

public record ErrorDetailDto(
    string Code,
    string Message,
    string? Details = null,
    DateTime Timestamp = default)
{
    public ErrorDetailDto(string code, string message, string? details = null)
        : this(code, message, details, DateTime.UtcNow) { }
}
