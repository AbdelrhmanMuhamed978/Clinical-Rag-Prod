using System.ComponentModel.DataAnnotations;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/config")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
[RequestSizeLimit(512 * 1024)]
public class ConfigController : ControllerBase
{
    private readonly IConfigService _cfg;
    private readonly ICurrentUser _currentUser;
    private readonly AppDbContext _db;
    public ConfigController(IConfigService cfg, ICurrentUser currentUser, AppDbContext db)
    { _cfg = cfg; _currentUser = currentUser; _db = db; }

    [HttpGet("published")]
    public async Task<IActionResult> Published(Guid workspaceId) => Ok(await _cfg.GetPublishedAsync(workspaceId));

    [HttpGet("versions")]
    public async Task<IActionResult> Versions(Guid workspaceId)
    {
        var userId = await _currentUser.GetUserIdAsync();
        var role = await _db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.WorkspaceId == workspaceId && member.UserId == userId)
            .Select(member => member.Role).FirstAsync();
        return Ok(new { role, versions = await _cfg.GetVersionsAsync(workspaceId) });
    }

    [HttpPost("draft")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Draft(Guid workspaceId, [FromBody] DraftDto dto)
    {
        var u = await _currentUser.EnsureUserAsync();
        return await MutateAsync(async () => await _cfg.CreateDraftAsync(workspaceId, dto.ConfigJson, u.Id, dto.PublishedConfigId));
    }

    [HttpPost("{id}/publish")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Publish(Guid workspaceId, Guid id) =>
        await MutateAsync(() => _cfg.PublishAsync(workspaceId, id));

    [HttpPost("{id}/discard")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Discard(Guid workspaceId, Guid id) =>
        await MutateAsync(() => _cfg.DiscardAsync(workspaceId, id));

    [HttpPost("rollback/{version:int}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Rollback(Guid workspaceId, int version)
    {
        var user = await _currentUser.EnsureUserAsync();
        return await MutateAsync(() => _cfg.RollbackAsync(workspaceId, version, user.Id));
    }

    private async Task<IActionResult> MutateAsync(Func<Task<ConfigVersion?>> mutation)
    {
        try
        {
            var result = await mutation();
            return result == null ? NotFound() : Ok(result);
        }
        catch (ConfigValidationException error) { return BadRequest(new { error = "Invalid configuration.", errors = error.Errors }); }
        catch (ConfigLifecycleException error) { return Conflict(new { error = error.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Configuration changed. Refresh versions and try again." }); }
        catch (DbUpdateException) { return Conflict(new { error = "Configuration changed. Refresh versions and try again." }); }
    }
}

public record DraftDto([Required, StringLength(ConfigValidator.MaxJsonLength, MinimumLength = 2)] string ConfigJson,
    Guid? PublishedConfigId = null);
