using System.ComponentModel.DataAnnotations;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalRag.Api.Controllers;

public sealed record ColabRegistrationDto(
    [Required, StringLength(2048)] string BaseUrl,
    [Required, StringLength(4096, MinimumLength = 16)] string ApiKey,
    [Required, StringLength(32, MinimumLength = 32)] string InstanceId,
    bool Heartbeat = false);
public sealed record ColabManualSettingDto(
    [Required, StringLength(2048)] string BaseUrl,
    [StringLength(4096)] string? ApiKey,
    bool ManualOverride,
    [Range(0, int.MaxValue - 1)] int ExpectedRevision);

[ApiController]
[RequestSizeLimit(16 * 1024)]
public sealed class ColabAdministrationController(ColabConnectionStore connections, IConfiguration configuration) : ControllerBase
{
    [Authorize]
    [HttpGet("/api/admin/access")]
    public IActionResult Access()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { isAdmin = PlatformAdmin.IsAllowed(User, configuration) });
    }

    [Authorize(Policy = PlatformAdmin.Policy)]
    [HttpGet("/api/admin/colab-settings")]
    public async Task<IActionResult> Settings(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await connections.StatusAsync(admin: true, token));
    }

    [Authorize(Policy = PlatformAdmin.Policy)]
    [HttpPut("/api/admin/colab-settings")]
    public async Task<IActionResult> Manual(ColabManualSettingDto setting, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            await connections.ManualAsync(setting.BaseUrl, setting.ApiKey, setting.ManualOverride, setting.ExpectedRevision, token);
            return Ok(await connections.StatusAsync(admin: true, token));
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (ColabSettingConflictException exception) { return Conflict(new { error = exception.Message, code = exception.Code }); }
    }

    [AllowAnonymous]
    [HttpPost("/api/admin/colab-registration")]
    public async Task<IActionResult> Register(ColabRegistrationDto registration, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ServiceSecrets.Matches(configuration["Colab:RegistrationSecret"], Request.Headers["X-Colab-Registration-Secret"].ToString()))
            return Unauthorized(new { error = "Colab registration is not authorized." });
        try
        {
            await connections.RegisterAsync(registration.BaseUrl, registration.ApiKey, registration.InstanceId, registration.Heartbeat, token);
            return Ok(new { registered = true }); // Never reflect URL, credentials or the registration payload.
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (ColabSettingConflictException exception) { return Conflict(new { error = exception.Message, code = exception.Code }); }
    }

    [AllowAnonymous]
    [HttpGet("/api/internal/colab-connection")]
    public async Task<IActionResult> InternalConnection(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ServiceSecrets.Matches(configuration["Pipeline:ApiKey"], Request.Headers["X-Pipeline-Key"].ToString()))
            return Unauthorized(new { error = "Service authentication required." });
        var connection = await connections.GetAsync(token);
        // Server-to-server only. A Firebase bearer, including an admin's, is not sufficient.
        return Ok(new { baseUrl = connection.BaseUri.AbsoluteUri, apiKey = connection.ApiKey, revision = connection.Revision });
    }
}
