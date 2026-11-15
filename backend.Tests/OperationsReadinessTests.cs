using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinicalRag.Tests;

public class OperationsReadinessTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Pipeline:ApiKey"] = "synthetic-pipeline-key", ["Pipeline:BaseUrl"] = "https://pipeline.test" }).Build();

    [Theory]
    [InlineData(true, true, 200)]
    [InlineData(true, false, 503)]
    [InlineData(false, true, 503)]
    public async Task Readiness_checks_database_colab_and_pipeline_with_safe_model_details(bool schema, bool python, int status)
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        if (schema) db.Database.EnsureCreated();
        var configuration = Configuration();
        using var handler = new DocumentHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new { ready = python }) }));
        var pipeline = new PipelineClient(configuration, new FakeHttpClientFactory(handler));
        var readiness = new ReadinessService(db, new FakeColabClient(), pipeline);
        var controller = new OperationsController(readiness, new FakeColabClient(), null!, configuration)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ObjectResult>(await controller.Ready(CancellationToken.None));
        Assert.Equal(status, result.StatusCode);
        var body = Assert.IsType<ApplicationReadiness>(result.Value);
        Assert.Equal(schema, body.Checks.Database.Ready); Assert.Equal(python, body.Checks.Pipeline.Ready);
        Assert.Equal(schema, body.Checks.Colab.Ready);
        if (schema) Assert.Equal("synthetic-model", body.Checks.Colab.Model);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(status == 503 ? "30" : "", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void Liveness_does_not_touch_dependencies_and_metrics_fail_closed()
    {
        var controller = new OperationsController(null!, null!, null!, Configuration())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<OkObjectResult>(controller.Health());
        Assert.Equal(403, Assert.IsType<ObjectResult>(controller.Metrics()).StatusCode);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Lines.Add(formatter(state, exception)); }
    }

    [Fact]
    public async Task Middleware_returns_sanitized_503_and_logs_neither_input_path_query_nor_exception_message()
    {
        var logger = new RecordingLogger<RequestObservabilityMiddleware>();
        var context = new DefaultHttpContext(); context.Request.Path = "/PRIVATE_CASE"; context.Request.QueryString = new("?key=PRIVATE_KEY");
        context.Request.Headers[RequestCorrelation.Header] = "PRIVATE_REQUEST_ID";
        context.Response.Body = new MemoryStream();
        var middleware = new RequestObservabilityMiddleware(_ => throw new DependencyUnavailableException("colab", 19), logger);
        await middleware.InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode); Assert.Equal("19", context.Response.Headers.RetryAfter.ToString());
        Assert.Matches("^[a-f0-9]{32}$", context.TraceIdentifier);
        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        Assert.Equal("colab", body.GetProperty("dependency").GetString());
        Assert.Equal(context.TraceIdentifier, body.GetProperty("requestId").GetString());
        Assert.DoesNotContain("PRIVATE", string.Join('\n', logger.Lines));

        using var timing = OperationTelemetry.Start("pipeline.analyze", logger, "PRIVATE_PROMPT"); timing.Success(); timing.Dispose();
        Assert.Contains(OperationTelemetry.InputHash("PRIVATE_PROMPT"), string.Join('\n', logger.Lines));
        Assert.DoesNotContain("PRIVATE_PROMPT", string.Join('\n', logger.Lines));
        Assert.Throws<ArgumentException>(() => OperationTelemetry.Record("PRIVATE_LABEL", "success", 1));
        Assert.DoesNotContain("PRIVATE", OperationTelemetry.Prometheus());
    }

    [Fact]
    public async Task Colab_readiness_forwards_correlation_and_only_model_credentials_and_bounds_metadata()
    {
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { TraceIdentifier = "deadbeefdeadbeefdeadbeefdeadbeef" } };
        using var handler = new DocumentHttpHandler((request, _) =>
        {
            Assert.Equal(context.HttpContext.TraceIdentifier, request.Headers.GetValues(RequestCorrelation.Header).Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                ready = true, model = "synthetic-model", embeddingModel = "synthetic-encoder", embeddingDimensions = 2,
                gpu = new { available = true, name = "synthetic-GPU", device = "cuda", totalMemoryBytes = 1024L }
            }) });
        });
        using var http = new HttpClient(handler);
        var client = new ColabClient(http, new TestColabConnectionProvider(Configuration()), Configuration(), context);
        var ready = await client.ReadyAsync();
        Assert.True(ready.Ready); Assert.Equal("synthetic-model", ready.Model);
        Assert.Equal("synthetic-GPU", ready.Gpu!.Name); Assert.Equal(1024L, ready.Gpu.TotalMemoryBytes);
    }
}
