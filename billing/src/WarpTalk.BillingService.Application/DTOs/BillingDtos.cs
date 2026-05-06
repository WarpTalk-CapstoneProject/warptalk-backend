using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

// ============================================================================
// RESPONSE DTOs
// ============================================================================

public record PlanDto(
    Guid Id,
    string Name,
    decimal Price,
    int CreditsPerMonth,
    bool IsActive = true,
    DateTime? CreatedAt = null);

public record SubscriptionDto(
    Guid Id,
    Guid WorkspaceId,
    Guid PlanId,
    string Status,
    int CurrentCredits,
    DateTime StartDate,
    DateTime? EndDate,
    DateTime CreatedAt);

public record WorkspaceCreditsDto(
    Guid WorkspaceId,
    int CurrentCredits,
    DateTime? SubscriptionEndDate,
    string SubscriptionStatus = "active");

public record CreditTransactionDto(
    Guid Id,
    Guid WorkspaceId,
    int Amount,
    string Type,
    Guid? ReferenceId,
    string? ReferenceType,
    DateTime CreatedAt);

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

public record ConsumeCreditsRequest(
    [Required(ErrorMessage = "Amount is required")]
    [Range(1, int.MaxValue, ErrorMessage = "Amount must be greater than 0")]
    int Amount,
    
    [Required(ErrorMessage = "Reference type is required")]
    string ReferenceType,
    
    Guid? ReferenceId = null);

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
