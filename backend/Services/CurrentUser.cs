using System.Security.Claims;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly AppDbContext _db;

    public CurrentUser(IHttpContextAccessor accessor, AppDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    public string? FirebaseUid =>
        _accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? _accessor.HttpContext?.User?.FindFirstValue("user_id");

    public string Email =>
        _accessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email)
        ?? _accessor.HttpContext?.User?.FindFirstValue("email") ?? string.Empty;

    public bool IsAuthenticated =>
        _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

    public async Task<User> EnsureUserAsync()
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(FirebaseUid))
            throw new UnauthorizedAccessException("An authenticated identity is required.");
        var uid = FirebaseUid;
        var email = Email.Trim();
        var verified = _accessor.HttpContext?.User?.Identity?.AuthenticationType != "Dev"
            && string.Equals(_accessor.HttpContext?.User?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)
            && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) && email.Length <= 256;
        // A separate unit of work cannot discard the caller's unsaved changes on an
        // invitation race. Retry only database acceptance, never an upstream request.
        for (var attempt = 0; ; attempt++)
        {
            await using var db = new AppDbContext(_db.ContextOptions, _db.VectorCache);
            try
            {
                var user = await db.Users.FirstOrDefaultAsync(x => x.FirebaseUid == uid);
                if (user == null) { user = new User { FirebaseUid = uid, Email = email.Length <= 256 ? email : "" }; db.Users.Add(user); }
                else if (verified) user.Email = email;
                await db.SaveChangesAsync();
                if (verified)
                {
                    var normalized = email.ToLowerInvariant();
                    var invitations = await db.WorkspaceInvitations.Include(i => i.Workspace)
                        .Where(i => i.Email == normalized && i.Workspace!.DeletedAt == null).ToListAsync();
                    foreach (var invite in invitations)
                    {
                        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == invite.WorkspaceId && m.UserId == user.Id))
                            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = invite.WorkspaceId, UserId = user.Id, Role = invite.Role });
                        // Existing membership is never upgraded by an old invitation.
                        invite.Workspace!.MembershipRevision = checked(invite.Workspace.MembershipRevision + 1);
                        db.WorkspaceInvitations.Remove(invite);
                        db.RuleAuditEntries.Add(new RuleAuditEntry { WorkspaceId = invite.WorkspaceId, ActorId = user.Id,
                            Action = "member_invite_accepted", AfterJson = System.Text.Json.JsonSerializer.Serialize(new { userId = user.Id, invitationId = invite.Id }) });
                    }
                    if (invitations.Count > 0) await db.SaveChangesAsync();
                }
                return user;
            }
            catch (DbUpdateException) when (attempt < 2) { /* Re-read pending invites and shared membership revisions. */ }
        }
    }

    public async Task<Guid> GetUserIdAsync()
    {
        var uid = FirebaseUid;
        if (string.IsNullOrEmpty(uid)) return Guid.Empty;
        if (!IsAuthenticated) return Guid.Empty;
        return (await EnsureUserAsync()).Id;
    }
}
