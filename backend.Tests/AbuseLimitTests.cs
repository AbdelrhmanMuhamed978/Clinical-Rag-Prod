using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClinicalRag.Tests;

[Collection("Sequential")]
public class AbuseLimitTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AbuseLimitTests(CustomWebApplicationFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> Configure(int requests, int windowSeconds, int concurrency,
        IPipelineClient? pipeline = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AbuseLimits:RequestsPerWindow"] = requests.ToString(),
                ["AbuseLimits:WindowSeconds"] = windowSeconds.ToString(),
                ["AbuseLimits:Concurrency"] = concurrency.ToString(),
                ["AbuseLimits:RequestTimeoutSeconds"] = "30"
            }));
            if (pipeline != null)
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPipelineClient>();
                    services.AddSingleton(pipeline);
                });
        });
    }

    private static async Task<(Guid WorkspaceId, ClaimsPrincipal Editor)> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Add(new User
        {
            FirebaseUid = $"abuse-{Guid.NewGuid():N}",
            Email = $"abuse-{Guid.NewGuid():N}@example.org"
        }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Synthetic abuse limits", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "editor" });
        await db.SaveChangesAsync();
        return (workspace.Id, new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.FirebaseUid),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("email_verified", "true")
        }, "Test")));
    }

    [Fact]
    public async Task Per_user_request_limit_returns_429_with_retry_after()
    {
        using var factory = Configure(requests: 2, windowSeconds: 60, concurrency: 2);
        var data = await SeedAsync(factory);
        using var client = factory.CreateClient();
        try
        {
            TestPrincipal.Current = data.Editor;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/tests/run", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/tests/run", new { })).StatusCode);
            var limited = await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/tests/run", new { });
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            var retryAfter = Assert.Single(limited.Headers.GetValues("Retry-After"));
            Assert.True(int.TryParse(retryAfter, out var seconds) && seconds >= 1);
            var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(seconds, body.GetProperty("retryAfterSeconds").GetInt32());
        }
        finally { TestPrincipal.Current = null; }
    }

    [Fact]
    public async Task Per_user_concurrency_limit_rejects_a_parallel_run()
    {
        var pipeline = new BlockingPipelineClient();
        using var factory = Configure(requests: 10, windowSeconds: 60, concurrency: 1, pipeline);
        var data = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await new ConfigService(db).GetPublishedAsync(data.WorkspaceId);
            db.TestCases.Add(new TestCase
            {
                WorkspaceId = data.WorkspaceId,
                Name = "Synthetic case",
                InputText = "Synthetic case input"
            });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic");
        try
        {
            TestPrincipal.Current = data.Editor;
            var first = client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/tests/run", new { });
            await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var second = await client.PostAsJsonAsync($"/api/workspaces/{data.WorkspaceId}/tests/run", new { });
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.True(second.Headers.Contains("Retry-After"));

            pipeline.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        }
        finally
        {
            TestPrincipal.Current = null;
            pipeline.Release.TrySetResult();
        }
    }

    [Fact]
    public void Expensive_analysis_and_test_routes_have_abuse_and_timeout_metadata()
    {
        var actions = new[]
        {
            (typeof(AnalysisController), nameof(AnalysisController.Analyze)),
            (typeof(AnalysisController), nameof(AnalysisController.Stream)),
            (typeof(TestsController), nameof(TestsController.Run))
        };
        foreach (var (type, name) in actions)
        {
            var method = type.GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttributes(typeof(AbuseLimitedAttribute), false).SingleOrDefault());
            var timeout = Assert.IsType<RequestTimeoutAttribute>(method.GetCustomAttributes(typeof(RequestTimeoutAttribute), false).Single());
            Assert.Equal("analysis", timeout.PolicyName);
        }

        var policies = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestTimeoutOptions>>().Value.Policies;
        Assert.True(policies.ContainsKey("analysis"));
    }

    private sealed class TimeoutFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AbuseLimits:RequestTimeoutSeconds", "1");
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddControllers().AddApplicationPart(typeof(TimeoutProbeController).Assembly));
        }
    }

    [Fact]
    public async Task Request_timeout_returns_504_without_running_the_full_work()
    {
        using var factory = new TimeoutFactory();
        using var client = factory.CreateClient();
        var started = DateTime.UtcNow;
        var response = await client.GetAsync("/api/security-test/slow");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    private sealed class BlockingPipelineClient : IPipelineClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement(new
            {
                plan_source = "single_question",
                problems = Array.Empty<object>(),
                not_covered = Array.Empty<object>(),
                warnings = Array.Empty<object>(),
                disclaimer = "Synthetic test result.",
                version = "synthetic"
            });
        }

        public async IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(PipelineAnalyzeRequest request,
            string? authorization, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
