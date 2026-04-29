using System;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Yêu cầu tạo link thanh toán
/// </summary>
public class CreatePaymentLinkRequest
{
    /// <summary>
    /// ID của gói cước muốn mua (Upgrade)
    /// </summary>
    public Guid? PlanId { get; set; }
    
    /// <summary>
    /// Số phút muốn nạp thêm (Top-up)
    /// </summary>
    public decimal? TopUpMinutes { get; set; }
}
