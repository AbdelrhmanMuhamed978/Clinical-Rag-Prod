using ClinicalRag.Api.Data;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Attributes;

/// <summary>
/// Viewer/editor/owner permissions are retained. Reviewer is a separate capability,
/// not a higher role: it permits rule review, not owner administration or editing.
/// </summary>
public enum WorkspaceRole { Viewer = 0, Editor = 1, Owner = 2, Reviewer = 3 }

/// <summary>
/// Authorizes access to a workspace-scoped resource.
///
/// Reads {workspaceId} from route data, checks the current user's WorkspaceMembers row,
/// returns 404 for non-members (workspace existence is not revealed) and 403 for insufficient role.
/// Stackable: apply [WorkspaceAuthorize(Viewer)] at class level and a stricter role on write actions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class WorkspaceAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly WorkspaceRole _minRole;

    public WorkspaceAuthorizeAttribute(WorkspaceRole minRole = WorkspaceRole.Viewer)
    {
        _minRole = minRole;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var http = context.HttpContext;
        var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var userId = await currentUser.GetUserIdAsync();
        if (userId == Guid.Empty)
        {
            context.Result = new NotFoundResult();
            return;
        }

        var routeData = context.RouteData;
        if (!routeData.Values.TryGetValue("workspaceId", out var raw) || raw == null)
        {
            context.Result = new NotFoundResult();
            return;
        }
        if (!Guid.TryParse(raw.ToString(), out var workspaceId))
        {
            context.Result = new NotFoundResult();
            return;
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var member = await db.WorkspaceMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId
                && m.Workspace != null && m.Workspace.DeletedAt == null);

        if (member == null)
        {
            // Non-member: do not reveal whether the workspace exists.
            context.Result = new NotFoundResult();
            return;
        }

        if (!Allows(member.Role, _minRole))
        {
            context.Result = new ForbidResult();
            return;
        }
    }

    public static bool Allows(string role, WorkspaceRole permission) => permission switch
    {
        WorkspaceRole.Viewer => role is "viewer" or "editor" or "owner" or "reviewer",
        WorkspaceRole.Editor => role is "editor" or "owner",
        WorkspaceRole.Owner => role == "owner",
        WorkspaceRole.Reviewer => role is "reviewer" or "owner",
        _ => false
    };
}
