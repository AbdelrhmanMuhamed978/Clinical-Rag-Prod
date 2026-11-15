using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/drugs")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
[RequestSizeLimit(512 * 1024)]
public class DrugCatalogController : ControllerBase
{
    private readonly RuleService _rules;
    private readonly ICurrentUser _user;
    public DrugCatalogController(RuleService rules, ICurrentUser user) { _rules = rules; _user = user; }

    [HttpGet]
    public async Task<IActionResult> Get(Guid workspaceId)
    {
        try { return Ok(await _rules.CatalogAsync(workspaceId, HttpContext.RequestAborted)); }
        catch (RuleValidationException) { return Conflict(new { error = "Stored drug recognition is invalid. Repair the workspace catalog before analysis." }); }
    }

    [HttpGet("repair")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Repair(Guid workspaceId)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await _rules.CatalogRepairAsync(workspaceId, HttpContext.RequestAborted)); }
        catch (RuleLifecycleException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }

    [HttpPatch]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Update(Guid workspaceId, [FromBody] UpdateDrugCatalogDto dto)
    {
        try { return Ok(await _rules.UpdateCatalogAsync(workspaceId, dto, await _user.GetUserIdAsync(), HttpContext.RequestAborted)); }
        catch (RuleValidationException error) { return BadRequest(new { error = "Invalid drug catalog.", errors = error.Errors }); }
        catch (RuleLifecycleException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
        catch (DbUpdateException) { return Conflict(new { error = "Drug catalog changed. Reload before trying again." }); }
    }
}
