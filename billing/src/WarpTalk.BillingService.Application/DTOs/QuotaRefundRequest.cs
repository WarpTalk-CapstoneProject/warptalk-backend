using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// DTO cho yêu cầu hoàn trả số phút quota.
/// </summary>
public class QuotaRefundRequest
{

    /// <summary>
    /// ID của phiên họp hoặc giao dịch gốc cần hoàn trả.
    /// </summary>
    [Required]
    public Guid SessionId { get; set; }

    /// <summary>
    /// Số phút muốn hoàn trả.
    /// </summary>
    [Range(0.01, 10000)]
    public decimal RefundedMinutes { get; set; }

    /// <summary>
    /// Lý do hoàn trả (ví dụ: WORKER_CRASH, USER_COMPLAINT).
    /// </summary>
    public string? Reason { get; set; }
}
