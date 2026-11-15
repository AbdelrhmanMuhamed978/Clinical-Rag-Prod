using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalRag.Api.Controllers;

[ApiController]
public sealed class OperationsController(ReadinessService readiness, IColabClient colab, ColabConnectionStore connections,
    IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { alive = true });

    [AllowAnonymous]
    [HttpGet("/ready")]
    public async Task<IActionResult> Ready(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await readiness.CheckAsync(token);
        if (!result.Ready) Response.Headers.RetryAfter = "30";
        return StatusCode(result.Ready ? 200 : 503, result);
    }

    [AllowAnonymous]
    [HttpGet("/metrics")]
    public IActionResult Metrics()
    {
        Response.Headers.CacheControl = "no-store";
        if (!PlatformAdmin.IsAllowed(User, configuration)
            && !ServiceSecrets.Matches(configuration["Operations:MetricsKey"], Request.Headers["X-Operations-Key"].ToString()))
            return StatusCode(403, new { error = "Metrics access is not authorized." });
        return Content(OperationTelemetry.Prometheus(), "text/plain; version=0.0.4; charset=utf-8");
    }

    [Authorize]
    [HttpGet("/api/operations/colab")]
    public async Task<IActionResult> ColabStatus(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!string.Equals(User.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase)) return Forbid();
        var connection = await connections.StatusAsync(admin: false, token);
        var model = await colab.ReadyAsync(token);
        return Ok(new { connection, model }); // No base URL, plaintext key, protected key or instance ID.
    }
}
