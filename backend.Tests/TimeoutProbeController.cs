using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalRag.Tests;

/// <summary>Test-only endpoint; never part of the production application assembly.</summary>
[ApiController]
[Route("/api/security-test")]
public sealed class TimeoutProbeController : ControllerBase
{
    [HttpGet("slow")]
    [RequestTimeout("analysis")]
    public async Task<IActionResult> Slow()
    {
        await Task.Delay(TimeSpan.FromSeconds(30), HttpContext.RequestAborted);
        return Ok();
    }
}
