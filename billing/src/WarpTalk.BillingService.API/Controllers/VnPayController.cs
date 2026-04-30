using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/vnpay")]
public class VnPayController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public VnPayController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    [AllowAnonymous]
    [HttpGet("ipn")]
    public async Task<IActionResult> HandleIpn(CancellationToken cancellationToken)
    {
        // VNPay integration not yet implemented
        // This endpoint is reserved for future VNPay IPN webhook handling
        return new ObjectResult(new { code = "01", message = "VNPay integration not yet implemented" })
        {
            StatusCode = 501
        };
    }
}
