using System;
using System.Threading.Tasks;

namespace WarpTalk.PaymentService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<string> CreateCheckoutSessionAsync(Guid userId, decimal amount, string currency, string paymentType);
}
