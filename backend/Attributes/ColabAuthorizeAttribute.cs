using System.Security.Claims;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Attributes;

/// <summary>
/// Authorizes access to the Colab proxy endpoints (api/colab/*).
///
/// Requires an authenticated, email-verified user who is an editor or owner
/// in at least one workspace. The Colab routes carry no workspaceId, so access
/// is based on membership in any workspace rather than a specific workspace.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class ColabAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
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

        // Firebase provides the email_verified claim; required before proxying to Colab.
        var emailVerified = http.User?.FindFirstValue("email_verified");
        if (!string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ForbidResult();
            return;
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var hasEditorAccess = await db.WorkspaceMembers
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId
                && (m.Role == "editor" || m.Role == "owner"));

        if (!hasEditorAccess)
        {
            context.Result = new ForbidResult();
            return;
        }
    }
}
