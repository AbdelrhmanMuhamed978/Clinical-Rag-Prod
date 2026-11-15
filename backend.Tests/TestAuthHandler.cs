using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicalRag.Tests;

/// <summary>
/// Test authentication handler. Resolves the ambient <see cref="TestPrincipal.Current"/>
/// so tests can drive any identity through the real authorization pipeline.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = TestPrincipal.Current;
        if (principal == null) return Task.FromResult(AuthenticateResult.NoResult());
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class TestPrincipal
{
    public static ClaimsPrincipal? Current { get; set; }
}
