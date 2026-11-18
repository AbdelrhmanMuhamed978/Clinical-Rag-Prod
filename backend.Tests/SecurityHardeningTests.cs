using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class CorsAndSecurityHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CorsAndSecurityHeaderTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed class ConfiguredFactory(string environment, Dictionary<string, string?> settings)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        }
    }

    private static ConfiguredFactory WithOrigins(params string[] origins)
    {
        var values = new Dictionary<string, string?>
        {
            ["ClinicalRag:UseInMemory"] = "true",
            ["Database:AutoMigrate"] = "false",
            ["Documents:AutomaticParsing"] = "false",
            ["Documents:AutomaticFileCleanup"] = "false"
        };
        for (var i = 0; i < origins.Length; i++) values[$"Cors:AllowedOrigins:{i}"] = origins[i];
        return new ConfiguredFactory("Development", values);
    }

    [Fact]
    public async Task Preflight_from_an_allowed_origin_is_accepted_and_exposes_safe_headers()
    {
        using var factory = WithOrigins("https://allowed.example", "https://second.example");
        Assert.Equal(new[] { "https://allowed.example", "https://second.example" },
            factory.Services.GetRequiredService<IConfiguration>().GetSection("Cors:AllowedOrigins").Get<string[]>());
        var policy = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>()
            .Value.GetPolicy("App")!;
        Assert.Contains("https://allowed.example", policy.Origins);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/workspaces");
        request.Headers.Add("Origin", "https://allowed.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins),
            string.Join(" | ", response.Headers.Select(header => $"{header.Key}={string.Join(",", header.Value)}")));
        Assert.Equal("https://allowed.example", Assert.Single(origins!));

        using var actual = new HttpRequestMessage(HttpMethod.Get, "/health");
        actual.Headers.Add("Origin", "https://allowed.example");
        var actualResponse = await client.SendAsync(actual);
        var exposed = Assert.Single(actualResponse.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("Retry-After", exposed);
        Assert.Contains("X-Request-Id", exposed);
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_does_not_receive_cors_permission()
    {
        using var factory = WithOrigins("https://allowed.example");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/workspaces");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Api_responses_set_nosniff_referrer_policy_and_a_deny_all_csp()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("*", csp);
    }

    [Fact]
    public async Task Production_accepts_an_explicitly_configured_origin()
    {
        var values = new Dictionary<string, string?>
        {
            ["ClinicalRag:UseInMemory"] = "true",
            ["Database:AutoMigrate"] = "false",
            ["Documents:AutomaticParsing"] = "false",
            ["Documents:AutomaticFileCleanup"] = "false",
            ["Cors:AllowedOrigins:0"] = "https://allowed.example"
        };
        using var factory = new ConfiguredFactory("Production", values);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/workspaces");
        request.Headers.Add("Origin", "https://allowed.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://allowed.example", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public void Production_startup_rejects_missing_firebase_project()
    {
        var values = new Dictionary<string, string?>
        {
            ["ClinicalRag:UseInMemory"] = "true",
            ["Database:AutoMigrate"] = "false",
            ["Documents:AutomaticParsing"] = "false",
            ["Documents:AutomaticFileCleanup"] = "false",
            ["Cors:AllowedOrigins:0"] = "https://allowed.example",
            ["Firebase:ProjectId"] = ""
        };
        using var factory = new ConfiguredFactory("Production", values);
        var error = Record.Exception(() => factory.Services);
        Assert.NotNull(error);
        Assert.Contains("Firebase:ProjectId", error!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_startup_rejects_missing_or_wildcard_allowed_origins(bool wildcard)
    {
        var values = new Dictionary<string, string?>
        {
            ["ClinicalRag:UseInMemory"] = "true",
            ["Database:AutoMigrate"] = "false",
            ["Documents:AutomaticParsing"] = "false",
            ["Documents:AutomaticFileCleanup"] = "false"
        };
        if (wildcard) values["Cors:AllowedOrigins:0"] = "*";
        using var factory = new ConfiguredFactory("Production", values);

        var error = Record.Exception(() => factory.Services);
        Assert.NotNull(error);
        Assert.Contains("Cors:AllowedOrigins", error!.ToString());
    }
}

/// <summary>Exercises the real JwtBearer pipeline with a local signed issuer, not the production key set.</summary>
public sealed class JwtAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Audience = "clinical-rag-security-test";
    public const string Issuer = "https://securetoken.google.com/clinical-rag-security-test";
    public static readonly SymmetricSecurityKey Key =
        new(Encoding.UTF8.GetBytes("synthetic-security-test-signing-key-32-bytes-minimum"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClinicalRag:UseInMemory"] = "true",
            ["Database:AutoMigrate"] = "false",
            ["Firebase:ProjectId"] = Audience,
            ["Documents:AutomaticParsing"] = "false",
            ["Documents:AutomaticFileCleanup"] = "false",
            ["Colab:TimeoutSeconds"] = "30"
        }));
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IColabConnectionProvider, TestColabConnectionProvider>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null!;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = Key,
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });
        });
    }

    public static string Token(string uid, string email, bool verified)
    {
        var header = new JwtHeader(new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        var payload = new JwtPayload
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = uid,
            ["user_id"] = uid,
            ["email"] = email,
            ["email_verified"] = verified,
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }
}

[Collection("Sequential")]
public class FirebaseJwtAuthenticationTests : IClassFixture<JwtAuthWebApplicationFactory>
{
    private readonly JwtAuthWebApplicationFactory _factory;

    public FirebaseJwtAuthenticationTests(JwtAuthWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Verified_new_user_is_accepted_and_created()
    {
        var uid = $"verified-{Guid.NewGuid():N}";
        using var client = Client(JwtAuthWebApplicationFactory.Token(uid, $"{uid}@example.org", verified: true));

        var response = await client.GetAsync("/api/workspaces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.SingleOrDefault(item => item.FirebaseUid == uid);
        Assert.NotNull(user);
        Assert.Equal($"{uid}@example.org", user!.Email);
    }

    [Fact]
    public async Task Unverified_email_token_is_rejected_before_user_bootstrap()
    {
        var uid = $"unverified-{Guid.NewGuid():N}";
        using var client = Client(JwtAuthWebApplicationFactory.Token(uid, $"{uid}@example.org", verified: false));

        var response = await client.GetAsync("/api/workspaces");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(db.Users.AsEnumerable(), item => item.FirebaseUid == uid);
    }
}
