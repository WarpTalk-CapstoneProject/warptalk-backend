using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentEventHandler
{
    bool CanHandle(PaymentEventContext context);

    Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default);
}
