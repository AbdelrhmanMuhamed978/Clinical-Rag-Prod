using System.Text.Json;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/analyses")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AnalysesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public AnalysesController(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] bool mine = true)
    {
        if (!mine)
            return BadRequest("Only mine=true is supported for privacy.");
        var user = await _currentUser.EnsureUserAsync();
        var runs = await _db.AnalysisRuns.AsNoTracking()
            .Where(run => run.WorkspaceId == workspaceId && run.UserId == user.Id)
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => new
            {
                run.Id,
                run.WorkspaceId,
                run.ConfigVersion,
                run.InputHash,
                run.CreatedAt,
                hasResult = run.ResultJson != null
            })
            .ToListAsync();
        return Ok(runs);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Open(Guid workspaceId, Guid id)
    {
        var user = await _currentUser.EnsureUserAsync();
        var run = await _db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Id == id && item.WorkspaceId == workspaceId && item.UserId == user.Id);
        if (run == null) return NotFound();
        JsonElement? result = null;
        if (!string.IsNullOrWhiteSpace(run.ResultJson))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<JsonElement>(run.ResultJson);
                var caseMode = stored.TryGetProperty("plan_source", out var source)
                    && source.GetString() is not ("single_question" or "chat");
                result = PipelineResultPolicy.ForStorage(stored, caseMode);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                // Legacy free-text results are not verified answers. Keep the audit
                // metadata accessible without returning their content or deleting it.
            }
        }
        return Ok(new
        {
            run.Id,
            run.WorkspaceId,
            run.ConfigVersion,
            run.InputHash,
            run.CreatedAt,
            result
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id)
    {
        var user = await _currentUser.EnsureUserAsync();
        var run = await _db.AnalysisRuns.FirstOrDefaultAsync(item =>
            item.Id == id && item.WorkspaceId == workspaceId && item.UserId == user.Id);
        if (run == null) return NotFound();
        _db.AnalysisRuns.Remove(run);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
