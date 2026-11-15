using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace ClinicalRag.Api.Services;

public interface IFirebaseTokenValidator
{
    Task<ClaimsPrincipal?> ValidateAsync(string idToken);
}

public class FirebaseTokenValidator : IFirebaseTokenValidator
{
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http = new();
    private JsonWebKeySet? _keys;
    private DateTime _keysFetched = DateTime.MinValue;

    public FirebaseTokenValidator(IConfiguration cfg) { _cfg = cfg; }

    public async Task<ClaimsPrincipal?> ValidateAsync(string idToken)
    {
        var projectId = _cfg["Firebase:ProjectId"];
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        try
        {
            if (_keys == null || (DateTime.UtcNow - _keysFetched).TotalHours > 6)
            {
                var json = await _http.GetStringAsync("https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com");
                _keys = new JsonWebKeySet(json);
                _keysFetched = DateTime.UtcNow;
            }
            var handler = new JwtSecurityTokenHandler();
            var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = $"https://securetoken.google.com/{projectId}",
                ValidateAudience = true,
                ValidAudience = projectId,
                ValidateLifetime = true,
                IssuerSigningKeys = _keys.GetSigningKeys()
            });
            return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
        }
        catch { return null; }
    }
}
