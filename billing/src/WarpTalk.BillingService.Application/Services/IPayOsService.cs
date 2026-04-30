using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// PayOS payment gateway service interface
/// </summary>
public interface IPayOsService
{
    /// <summary>
    /// Create a payment link in PayOS
    /// </summary>
    Task<PayOsCheckoutResponse> CreateCheckoutLinkAsync(
        long orderCode,
        long amount,
        string description,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get payment link details from PayOS
    /// </summary>
    Task<PayOsOrderDetailsResponse> GetCheckoutLinkDetailsAsync(
        long orderCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a payment link
    /// </summary>
    Task<PayOsCancelResponse> CancelCheckoutLinkAsync(
        long orderCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refund a payment
    /// </summary>
    Task<PayOsRefundResponse> RefundAsync(
        string orderCode,
        long amount,
        string reason,
        CancellationToken cancellationToken = default);
}
