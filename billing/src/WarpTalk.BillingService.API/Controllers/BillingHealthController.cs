using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.API.Controllers;

[Route("api/health")]
[ApiController]
public class BillingHealthController : ControllerBase
{
    private readonly BillingDbContext _db;

    public BillingHealthController(BillingDbContext db)
    {
        _db = db;
    }

    // ===================================================
    // BASIC HEALTH CHECK
    // ===================================================
    [HttpGet]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            service = "billing-service",
            timestamp = DateTime.UtcNow
        });
    }

    // ===================================================
    // DATABASE HEALTH CHECK
    // ===================================================
    [HttpGet("db")]
    public async Task<IActionResult> DatabaseHealth(CancellationToken ct)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);

            return Ok(new
            {
                database = canConnect ? "connected" : "failed",
                status = canConnect ? "healthy" : "unhealthy"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                status = "unhealthy",
                error = ex.Message
            });
        }
    }

    // ===================================================
    // LIVENESS CHECK (K8S / Docker)
    // ===================================================
    [HttpGet("live")]
    public IActionResult Liveness()
    {
        return Ok(new
        {
            status = "alive"
        });
    }

    // ===================================================
    // READINESS CHECK
    // ===================================================
    [HttpGet("ready")]
    public async Task<IActionResult> Readiness(CancellationToken ct)
    {
        var dbReady = await _db.Database.CanConnectAsync(ct);

        return Ok(new
        {
            status = dbReady ? "ready" : "not-ready",
            database = dbReady
        });
    }
}