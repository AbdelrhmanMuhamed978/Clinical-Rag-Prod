using System.Text.Json;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/colab")]
[Authorize]
[ColabAuthorize]
[RequestSizeLimit(10_000_000)]
public class ColabController : ControllerBase
{
    private readonly IColabClient _colab;
    public ColabController(IColabClient colab) { _colab = colab; }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var readiness = await _colab.ReadyAsync(cancellationToken);
        return Ok(new { healthy = readiness.Ready, readiness.Model, readiness.EmbeddingModel, readiness.EmbeddingDimensions, readiness.Gpu, readiness.Message });
    }

    [HttpPost("chunk/profile")]
    public async Task<IActionResult> Profile([FromBody] JsonElement body) =>
        Ok(await _colab.PostRawAsync("chunk/profile", body, HttpContext.RequestAborted));

    [HttpPost("chunk/preview")]
    public async Task<IActionResult> Preview([FromBody] JsonElement body) =>
        Ok(await _colab.PostRawAsync("chunk/preview", body, HttpContext.RequestAborted));

    [HttpPost("chunk/run")]
    public async Task<IActionResult> Run([FromBody] JsonElement body) =>
        Ok(await _colab.PostRawAsync("chunk/run", body, HttpContext.RequestAborted));
}
