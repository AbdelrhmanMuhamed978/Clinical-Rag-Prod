using System.Text.Json;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
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

    [HttpPost("{id:guid}/flag")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Flag(Guid workspaceId, Guid id, [FromBody] FlagAnswerDto dto)
    {
        var reason = (dto.Reason ?? "").Trim().ToLowerInvariant();
        if (!FlagReasons.Contains(reason)) return BadRequest(new { error = "Choose a supported flag reason." });
        var note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        if (note is { Length: > 1000 } || note?.Any(char.IsControl) == true)
            return BadRequest(new { error = "The optional note must be at most 1000 characters without control characters." });
        var run = await _db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Id == id && item.WorkspaceId == workspaceId);
        if (run == null) return NotFound();
        var user = await _currentUser.EnsureUserAsync();
        _db.AnswerFlags.Add(new AnswerFlag
        {
            WorkspaceId = workspaceId, AnalysisRunId = run.Id, ConfigVersion = run.ConfigVersion,
            Reason = reason, Note = note, CreatedBy = user.Id
        });
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { flagged = true });
    }

    [HttpGet("/api/workspaces/{workspaceId}/flags")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Flags(Guid workspaceId)
    {
        var flags = await _db.AnswerFlags.AsNoTracking()
            .Where(flag => flag.WorkspaceId == workspaceId)
            .OrderByDescending(flag => flag.CreatedAt)
            .Select(flag => new
            {
                flag.Id, flag.AnalysisRunId, flag.ConfigVersion, flag.Reason, flag.Note,
                flag.CreatedBy, flag.CreatedAt
            }).ToListAsync(HttpContext.RequestAborted);
        return Ok(new { flags });
    }

    private static readonly HashSet<string> FlagReasons = new(StringComparer.Ordinal)
    {
        "incorrect", "unsafe", "outdated", "missing-context", "citation-mismatch", "other"
    };
}

public record FlagAnswerDto(string? Reason, string? Note);
