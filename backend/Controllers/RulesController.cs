using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/rules")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
[RequestSizeLimit(1024 * 1024)]
public class RulesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly RuleService _rules;
    private readonly IRulePreviewClient _preview;
    public RulesController(AppDbContext db, ICurrentUser currentUser, RuleService rules, IRulePreviewClient preview)
    { _db = db; _currentUser = currentUser; _rules = rules; _preview = preview; }

    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId) =>
        Ok(await _db.Rules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.Title).ToListAsync(HttpContext.RequestAborted));

    [HttpGet("approved-snapshot")]
    public async Task<IActionResult> ApprovedSnapshot(Guid workspaceId) =>
        Ok(await _rules.ApprovedSnapshotAsync(workspaceId, HttpContext.RequestAborted));

    [HttpGet("access")]
    public async Task<IActionResult> Access(Guid workspaceId)
    {
        var userId = await _currentUser.GetUserIdAsync();
        var role = await _db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.UserId == userId).Select(m => m.Role).FirstAsync();
        return Ok(new { userId, role, canEdit = WorkspaceAuthorizeAttribute.Allows(role, WorkspaceRole.Editor),
            canReview = WorkspaceAuthorizeAttribute.Allows(role, WorkspaceRole.Reviewer), canDelete = role == "owner" });
    }

    [HttpGet("vocabulary")]
    public IActionResult Vocabulary(Guid workspaceId) => Ok(RuleAssets.Vocabulary);

    [HttpGet("{id:guid}/audit")]
    public Task<IActionResult> Audit(Guid workspaceId, Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 50) =>
        AuditPageAsync(workspaceId, id, skip, take);

    [HttpGet("audit")]
    public Task<IActionResult> WorkspaceAudit(Guid workspaceId, [FromQuery] int skip = 0, [FromQuery] int take = 50) =>
        AuditPageAsync(workspaceId, null, skip, take);

    [HttpPost]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] CreateRuleDto dto) =>
        await MutateAsync(async () => await _rules.CreateAsync(workspaceId, dto, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted));

    [HttpPost("import-default")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Import(Guid workspaceId) =>
        await MutateAsync(async () => await _rules.ImportDefaultsAsync(workspaceId, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted));

    [HttpPatch("{id:guid}")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Update(Guid workspaceId, Guid id, [FromBody] UpdateRuleDto dto) =>
        await MutateAsync(async () => await _rules.UpdateAsync(workspaceId, id, dto, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted));

    [HttpPost("{id:guid}/submit")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Submit(Guid workspaceId, Guid id, [FromBody] RuleRevisionDto dto) => await TransitionAsync(workspaceId, id, "submit", dto);

    [HttpPost("{id:guid}/approve")]
    [WorkspaceAuthorize(WorkspaceRole.Reviewer)]
    public async Task<IActionResult> Approve(Guid workspaceId, Guid id, [FromBody] RuleRevisionDto dto) => await TransitionAsync(workspaceId, id, "approve", dto);

    [HttpPost("{id:guid}/retire")]
    [WorkspaceAuthorize(WorkspaceRole.Reviewer)]
    public async Task<IActionResult> Retire(Guid workspaceId, Guid id, [FromBody] RuleRevisionDto dto) => await TransitionAsync(workspaceId, id, "retire", dto);

    [HttpPost("{id:guid}/reopen")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Reopen(Guid workspaceId, Guid id, [FromBody] RuleRevisionDto dto) => await TransitionAsync(workspaceId, id, "reopen", dto);

    [HttpPost("{id:guid}/enablement")]
    [WorkspaceAuthorize(WorkspaceRole.Reviewer)]
    public async Task<IActionResult> Enablement(Guid workspaceId, Guid id, [FromBody] RuleEnablementDto dto) =>
        await MutateAsync(async () => await _rules.SetEnabledAsync(workspaceId, id, dto, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted));

    [HttpDelete("{id:guid}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, [FromQuery] int? expectedRevision)
    {
        try { return await _rules.DeleteAsync(workspaceId, id, expectedRevision, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted) ? NoContent() : NotFound(); }
        catch (RuleLifecycleException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
        catch (DbUpdateException) { return Conflict(new { error = "Rule changed. Reload before trying again." }); }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(Guid workspaceId, [FromBody] RulePreviewDto dto)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var rule = dto.Rule.ToRule();
            RuleValidator.Validate(rule);
            var catalog = await _rules.CatalogAsync(workspaceId, HttpContext.RequestAborted);
            if (dto.ExpectedCatalogRevision != null && dto.ExpectedCatalogRevision != catalog.Revision)
                return Conflict(new { error = "Drug catalog changed. Refresh recognition before previewing." });
            var result = await _preview.PreviewAsync(new PipelineRulePreviewRequest(dto.Text, RuleValidator.ToPipeline(rule),
                catalog.Catalog, dto.RegexSample, catalog.Revision), Request.Headers.Authorization.ToString(), HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (RuleValidationException error) { return BadRequest(new { error = "Invalid rule definition.", errors = error.Errors }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499); }
        catch (DependencyUnavailableException unavailable)
        {
            Response.Headers.RetryAfter = unavailable.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(503, new { error = unavailable.PublicMessage, dependency = unavailable.Service });
        }
        catch (Exception) { return StatusCode(502, new { error = "Canonical preview is unavailable. No approval or result was stored." }); }
    }

    private async Task<IActionResult> TransitionAsync(Guid workspaceId, Guid id, string action, RuleRevisionDto dto) =>
        await MutateAsync(async () => await _rules.TransitionAsync(workspaceId, id, action, dto.ExpectedRevision, await _currentUser.GetUserIdAsync(), HttpContext.RequestAborted));

    private async Task<IActionResult> AuditPageAsync(Guid workspaceId, Guid? id, int skip, int take)
    {
        if (skip < 0 || take is < 1 or > 200) return BadRequest(new { error = "Use a nonnegative skip and a take from 1 to 200." });
        var entries = await _db.RuleAuditEntries.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && (id == null || a.RuleId == id))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Skip(skip).Take(take + 1).ToListAsync(HttpContext.RequestAborted);
        var hasMore = entries.Count > take;
        return Ok(new { entries = entries.Take(take), hasMore, nextSkip = hasMore ? (int?) (skip + take) : null });
    }

    private async Task<IActionResult> MutateAsync<T>(Func<Task<T?>> mutation)
    {
        try { var result = await mutation(); return result == null ? NotFound() : Ok(result); }
        catch (RuleValidationException error) { return BadRequest(new { error = "Invalid rule definition.", errors = error.Errors }); }
        catch (RuleLifecycleException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
        catch (DbUpdateException) { return Conflict(new { error = "Rule or drug catalog changed. Reload before trying again." }); }
    }
}
