using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace WarpTalk.BillingService.API.Controllers
{
    [ApiController]
    [Route("api/v1/billing/usage-events")]
    public class UsageEventController : ControllerBase
    {
        // ===================================================
        // INGEST USAGE EVENT (AI / API / TOKEN CONSUMPTION)
        // ===================================================
        [HttpPost]
        public IActionResult IngestUsageEvent([FromBody] object payload)
        {
            // TODO: integrate later with quota engine
            return Ok(new
            {
                message = "Usage event received",
                receivedAt = DateTime.UtcNow
            });
        }
    }
}