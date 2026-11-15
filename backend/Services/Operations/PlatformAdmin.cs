using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ClinicalRag.Api.Services;

public static class PlatformAdmin
{
    public const string Policy = "PlatformAdmin";
    public static bool IsAllowed(ClaimsPrincipal user, IConfiguration configuration)
    {
        if (user.Identity?.IsAuthenticated != true
            || !string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase)) return false;
        var uid = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("user_id") ?? user.FindFirstValue("sub");
        // Dev auth and workspace ownership do not bootstrap a platform administrator.
        if (string.IsNullOrWhiteSpace(configuration["Firebase:ProjectId"]) || string.IsNullOrWhiteSpace(uid)) return false;
        return (configuration.GetSection("Operations:AdminFirebaseUids").Get<string[]>() ?? [])
            .Any(allowed => !string.IsNullOrWhiteSpace(allowed) && StringComparer.Ordinal.Equals(allowed, uid));
    }
}
