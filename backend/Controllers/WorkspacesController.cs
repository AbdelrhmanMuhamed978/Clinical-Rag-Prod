using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
[Authorize]
public class WorkspacesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IConfiguration _cfg;
    public WorkspacesController(AppDbContext db, ICurrentUser currentUser, IConfiguration cfg)
    {
        _db = db;
        _currentUser = currentUser;
        _cfg = cfg;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!_currentUser.IsAuthenticated) return Unauthorized();
        var userId = await _currentUser.GetUserIdAsync();
        if (userId == Guid.Empty) return Unauthorized();
        var ids = await _db.WorkspaceMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync();
        var ws = await _db.Workspaces.Where(w => ids.Contains(w.Id) && w.DeletedAt == null).ToListAsync();
        return Ok(ws);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceDto dto)
    {
        if (!_currentUser.IsAuthenticated) return Unauthorized();
        if (!ValidName(dto.Name)) return BadRequest(new { error = "Enter a workspace name of 1–128 characters without control characters." });
        var u = await _currentUser.EnsureUserAsync();
        var w = new Workspace { Name = dto.Name.Trim(), OwnerId = u.Id };
        _db.Workspaces.Add(w);
        _db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = w.Id, UserId = u.Id, Role = "owner" });
        await _db.SaveChangesAsync();
        return Ok(w);
    }

    [HttpGet("{workspaceId}/members")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Members(Guid workspaceId)
    {
        var ms = await _db.WorkspaceMembers.Include(m => m.User).Where(m => m.WorkspaceId == workspaceId).ToListAsync();
        return Ok(ms.Select(m => new { m.UserId, m.User!.Email, m.Role }));
    }

    [HttpPatch("{workspaceId}/members/{userId:guid}/role")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> MemberRole(Guid workspaceId, Guid userId, [FromBody] UpdateMemberRoleDto dto)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: true);
        if (workspace == null) return NotFound();
        if (!ValidRole(dto.Role)) return BadRequest(new { error = "Choose viewer, editor, reviewer or owner access." });
        var member = await _db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, HttpContext.RequestAborted);
        if (member == null) return NotFound();
        if (dto.ExpectedRole == null) return StatusCode(428, new { error = "Reload the member and include expectedRole before changing access." });
        if (dto.ExpectedRole != member.Role) return Conflict(new { error = "Member access changed. Reload before trying again." });
        if (dto.Role == member.Role) return Ok(new { member.UserId, member.Role });
        if (member.Role == "owner" && dto.Role != "owner" && !await RetainOwner(workspace, member)) return LastOwner();
        var before = JsonSerializer.Serialize(new { member.UserId, member.Role }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        member.Role = dto.Role;
        Touch(workspace);
        _db.RuleAuditEntries.Add(new RuleAuditEntry { WorkspaceId = workspaceId, ActorId = await _currentUser.GetUserIdAsync(),
            Action = "member_role_updated", BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(new { member.UserId, member.Role }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Member access changed. Reload before trying again." }); }
        return Ok(new { member.UserId, member.Role });
    }

    [HttpGet("{workspaceId}")]
    [WorkspaceAuthorize]
    public async Task<IActionResult> Get(Guid workspaceId)
    {
        var workspace = await AuthorizedWorkspace(workspaceId);
        return workspace == null ? NotFound() : Ok(workspace);
    }

    [HttpGet("{workspaceId}/invitations")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Invitations(Guid workspaceId) => Ok(await _db.WorkspaceInvitations.AsNoTracking()
        .Where(i => i.WorkspaceId == workspaceId).OrderBy(i => i.CreatedAt)
        .Select(i => new { i.Id, i.Email, i.Role, i.CreatedAt }).ToListAsync());

    [HttpPost("{workspaceId}/invite")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Invite(Guid workspaceId, [FromBody] InviteMemberDto dto)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: true);
        if (workspace == null) return NotFound();
        var email = dto.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length > 256 || email.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(email) || !ValidRole(dto.Role))
            return BadRequest(new { error = "Enter a valid email and choose viewer, editor, reviewer or owner access." });
        if (await _db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.User!.Email.ToLower() == email))
            return Conflict(new { error = "This email already belongs to a workspace member." });
        if (await _db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == workspaceId && i.Email == email))
            return Conflict(new { error = "A pending invitation already exists. Cancel it before changing its access." });
        var invitation = new WorkspaceInvitation { WorkspaceId = workspaceId, Email = email, Role = dto.Role, InvitedBy = await _currentUser.GetUserIdAsync() };
        _db.WorkspaceInvitations.Add(invitation);
        Touch(workspace);
        Audit(workspaceId, invitation.InvitedBy, "member_invited", new { invitationId = invitation.Id, invitation.Role });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateException) { return Changed(); }
        return Ok(new { invitation.Id, invitation.Email, invitation.Role, status = "pending" });
    }

    [HttpDelete("{workspaceId}/invitations/{id:guid}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> CancelInvitation(Guid workspaceId, Guid id)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: true);
        if (workspace == null) return NotFound();
        var invite = await _db.WorkspaceInvitations.FirstOrDefaultAsync(i => i.Id == id && i.WorkspaceId == workspaceId);
        if (invite == null) return NotFound();
        _db.WorkspaceInvitations.Remove(invite); Touch(workspace);
        Audit(workspaceId, await _currentUser.GetUserIdAsync(), "member_invite_cancelled", new { invitationId = id });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return Ok(new { cancelled = true });
    }

    [HttpDelete("{workspaceId}/members/{userId:guid}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public Task<IActionResult> RemoveMember(Guid workspaceId, Guid userId, [FromQuery] string? expectedRole = null) =>
        Remove(workspaceId, userId, expectedRole, leaving: false);

    [HttpPost("{workspaceId}/leave")]
    [WorkspaceAuthorize]
    public async Task<IActionResult> Leave(Guid workspaceId) => await Remove(workspaceId, await _currentUser.GetUserIdAsync(), null, leaving: true);

    private async Task<IActionResult> Remove(Guid workspaceId, Guid userId, string? expectedRole, bool leaving)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: !leaving);
        if (workspace == null) return NotFound();
        var member = await _db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId);
        if (member == null) return NotFound();
        if (!leaving && expectedRole == null) return StatusCode(428, new { error = "Reload the member and include expectedRole before removing access." });
        if (!leaving && expectedRole != member.Role) return Changed();
        if (member.Role == "owner" && !await RetainOwner(workspace, member)) return LastOwner();
        _db.WorkspaceMembers.Remove(member); Touch(workspace);
        Audit(workspaceId, await _currentUser.GetUserIdAsync(), leaving ? "member_left" : "member_removed", new { userId });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return Ok(new { removed = true });
    }

    [HttpPatch("{workspaceId}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Rename(Guid workspaceId, [FromBody] RenameWorkspaceDto dto)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: true);
        if (workspace == null) return NotFound();
        if (!ValidName(dto.Name)) return BadRequest(new { error = "Enter a workspace name of 1–128 characters without control characters." });
        if (dto.ExpectedName == null) return StatusCode(428, new { error = "Reload and include expectedName before renaming." });
        if (dto.ExpectedName != workspace.Name) return Changed();
        workspace.Name = dto.Name.Trim(); Touch(workspace);
        Audit(workspaceId, await _currentUser.GetUserIdAsync(), "workspace_renamed", new { workspace.Name });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return Ok(workspace);
    }

    [HttpDelete("{workspaceId}")]
    [WorkspaceAuthorize(WorkspaceRole.Owner)]
    public async Task<IActionResult> Delete(Guid workspaceId)
    {
        var workspace = await AuthorizedWorkspace(workspaceId, owner: true);
        if (workspace == null) return NotFound();
        var documents = await _db.Documents.Where(d => d.WorkspaceId == workspaceId).ToListAsync();
        if (documents.Any(DocumentLifecycle.IsBusy)) return Conflict(new { error = "A document is being processed. Wait for processing to finish before deleting this workspace." });
        var ids = documents.Select(d => d.Id).ToList();
        _db.Chunks.RemoveRange(await _db.Chunks.Where(c => c.WorkspaceId == workspaceId).ToListAsync());
        _db.ChunkerGuidances.RemoveRange(await _db.ChunkerGuidances.Where(g => ids.Contains(g.DocumentId)).ToListAsync());
        _db.Documents.RemoveRange(documents);
        var queued = StoredDocumentFiles.Queue(_db, documents.Select(d => d.StoragePath));
        _db.WorkspaceInvitations.RemoveRange(await _db.WorkspaceInvitations.Where(i => i.WorkspaceId == workspaceId).ToListAsync());
        workspace.DeletedAt = DateTime.UtcNow; workspace.DeletedBy = await _currentUser.GetUserIdAsync(); Touch(workspace);
        workspace.EmbeddingModel = null; workspace.EmbeddingDimensions = null;
        // The workspace tombstone and audit/config/rule history remain; Restrict FKs
        // cannot cascade-delete evidence. Document revisions serialize active writers.
        Audit(workspaceId, workspace.DeletedBy.Value, "workspace_deleted", new { deleted = true });
        try { await _db.SaveChangesAsync(HttpContext.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        var failedFiles = await StoredDocumentFiles.CompleteAsync(_db, _cfg, queued);
        return Ok(new { deleted = true, documentsDeleted = documents.Count, filesNotDeleted = failedFiles,
            warning = failedFiles == 0 ? null : "Workspace content deleted, but some stored files could not be removed. Durable cleanup records are retained for safe retry or operator cleanup." });
    }

    private async Task<Workspace?> AuthorizedWorkspace(Guid id, bool owner = false)
    {
        // Read the shared concurrency token before access. A concurrent revocation,
        // deletion or owner removal makes this entire SaveChanges roll back.
        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == id && w.DeletedAt == null);
        if (workspace == null) return null;
        var actor = await _currentUser.GetUserIdAsync();
        return await _db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == id && m.UserId == actor && (!owner || m.Role == "owner")) ? workspace : null;
    }

    private async Task<bool> RetainOwner(Workspace workspace, WorkspaceMember removing)
    {
        var other = await _db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspace.Id && m.Role == "owner" && m.UserId != removing.UserId);
        if (other == null) return false;
        if (workspace.OwnerId == removing.UserId) workspace.OwnerId = other.UserId;
        return true;
    }
    private static void Touch(Workspace workspace) => workspace.MembershipRevision = checked(workspace.MembershipRevision + 1);
    private static bool ValidRole(string role) => role is "viewer" or "editor" or "reviewer" or "owner";
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 128 && !name.Any(char.IsControl);
    private IActionResult LastOwner() => Conflict(new { error = "The last workspace owner cannot be removed or demoted. Assign another owner first." });
    private IActionResult Changed() => Conflict(new { error = "Workspace or member access changed. Refresh before trying again." });
    private void Audit(Guid workspaceId, Guid actor, string action, object after) => _db.RuleAuditEntries.Add(new RuleAuditEntry {
        WorkspaceId = workspaceId, ActorId = actor, Action = action, AfterJson = JsonSerializer.Serialize(after, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
}

public record CreateWorkspaceDto(string Name);
public record UpdateMemberRoleDto(
    [Required, RegularExpression("^(viewer|editor|reviewer|owner)$")] string Role,
    [StringLength(16)] string? ExpectedRole = null);
public record InviteMemberDto([Required, EmailAddress, StringLength(256)] string Email,
    [Required, RegularExpression("^(viewer|editor|reviewer|owner)$")] string Role = "viewer");
public record RenameWorkspaceDto(string Name, string? ExpectedName = null);
