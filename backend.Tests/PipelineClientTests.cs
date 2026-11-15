using System.Net;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class PipelineClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class PendingBodyStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public CancellationToken ReadToken { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
                return ValueTask.FromResult(count);
            }
            ReadToken = cancellationToken; Reading.TrySetResult(true);
            return new(_pending.Task.WaitAsync(cancellationToken));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PipelineAnalyzeRequest Request() => new(
        "Which vasopressor?", JsonSerializer.SerializeToElement(new { }), Array.Empty<PipelineRule>(),
        new[] { new PipelineConversationTurn("Previous question", "Verified summary") },
        Guid.NewGuid().ToString(), "http://backend/retrieve", "conversation-1", RuleAssets.DefaultDrugs, 0);

    private static PipelineClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:BaseUrl"] = "http://pipeline.test", ["Pipeline:ApiKey"] = "pipeline-test-key"
        }).Build(), new Factory(new Handler(handle)));

    [Fact]
    public async Task Pipeline_transport_forwards_bearer_and_service_key_with_the_contract()
    {
        var called = false;
        var client = Client(async request =>
        {
            called = true;
            Assert.Equal("/analyze", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer user-token", request.Headers.Authorization!.ToString());
            Assert.Equal("pipeline-test-key", request.Headers.GetValues("X-Pipeline-Key").Single());
            Assert.False(request.Headers.Contains("X-Colab-Key"));
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Verified summary", body.GetProperty("history")[0].GetProperty("verifiedSummary").GetString());
            Assert.Empty(body.GetProperty("rules").EnumerateArray());
            Assert.True(body.GetProperty("drugCatalog").TryGetProperty("metformin", out _));
            Assert.Equal(0, body.GetProperty("catalogRevision").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new FakePipelineClient().Result.GetRawText(), Encoding.UTF8, "application/json")
            };
        });
        var result = await client.AnalyzeAsync(Request(), "Bearer user-token");
        Assert.True(called);
        Assert.True(result.TryGetProperty("problems", out _));
    }

    [Fact]
    public async Task Pipeline_transport_reads_multiline_progress_and_a_final_result()
    {
        var payload = new FakePipelineClient().Result.GetRawText();
        var client = Client(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(": keepalive\r\nevent: progress\r\ndata: {\"message\":\r\ndata: \"Checking quotes\"}\r\n\r\n"
                + $"event: result\r\ndata: {payload}\r\n\r\n", Encoding.UTF8, "text/event-stream")
        }));
        var events = new List<PipelineStreamEvent>();
        await foreach (var item in client.StreamAnalyzeAsync(Request(), "Bearer user-token")) events.Add(item);
        Assert.Equal(new[] { "progress", "result" }, events.Select(item => item.Event));
        Assert.Equal("Checking quotes", events[0].Data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Pipeline_transport_does_not_accept_an_incomplete_stream()
    {
        var client = Client(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: progress\ndata: {\"message\":\"Checking\"}\n\n", Encoding.UTF8, "text/event-stream")
        }));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var item in client.StreamAnalyzeAsync(Request(), "Bearer user-token")) { }
        });
    }

    [Fact]
    public async Task Preview_transport_is_private_post_with_exact_draft_definition_catalog_and_both_credentials()
    {
        var rule = RuleTestDefinitions.Create(enabled: false).ToRule();
        var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/rules/preview", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.CacheControl!.NoStore);
            Assert.Equal("Bearer preview-user-token", request.Headers.Authorization!.ToString());
            Assert.Equal("pipeline-test-key", request.Headers.GetValues("X-Pipeline-Key").Single());
            Assert.False(request.Headers.Contains("X-Colab-Key"));
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Synthetic Brand creat 2", body.GetProperty("text").GetString());
            Assert.Equal("Possible AKI", body.GetProperty("regexSample").GetString());
            Assert.Equal(7, body.GetProperty("catalogRevision").GetInt32());
            Assert.Equal(RuleTestDefinitions.Catalog.GetRawText(), body.GetProperty("drugCatalog").GetRawText());
            var definition = body.GetProperty("rule");
            Assert.Equal("draft", definition.GetProperty("status").GetString());
            Assert.False(definition.GetProperty("enabled").GetBoolean());
            Assert.Equal(rule.RuleId, definition.GetProperty("id").GetString());
            Assert.Equal("Test evidence.", definition.GetProperty("sources")[0].GetProperty("quote").GetString());
            Assert.True(definition.GetProperty("sources")[0].GetProperty("provenance").GetProperty("keep").GetBoolean());
            Assert.False(body.TryGetProperty("history", out _));
            Assert.False(body.TryGetProperty("retrieveUrl", out _));
            Assert.False(body.TryGetProperty("config", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"would_fire\":true,\"catalog_revision\":7}", Encoding.UTF8, "application/json")
            };
        });
        var result = await client.PreviewAsync(new PipelineRulePreviewRequest("Synthetic Brand creat 2",
            RuleValidator.ToPipeline(rule), RuleTestDefinitions.Catalog, "Possible AKI", 7), "Bearer preview-user-token");
        Assert.True(result.GetProperty("would_fire").GetBoolean());
    }

    [Fact]
    public async Task Preview_transport_rejects_upstream_errors_without_exposing_the_response_body()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("PRIVATE_CASE_MARKER", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.PreviewAsync(
            new PipelineRulePreviewRequest("PRIVATE_CASE_MARKER", RuleValidator.ToPipeline(RuleTestDefinitions.Create().ToRule()),
                RuleTestDefinitions.Catalog, "PRIVATE_REGEX_MARKER", 1), "Bearer user-token"));
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public async Task Preview_transport_propagates_request_cancellation()
    {
        var client = Client(_ => throw new InvalidOperationException("A cancelled request must not be sent."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PreviewAsync(
            new PipelineRulePreviewRequest("", RuleValidator.ToPipeline(RuleTestDefinitions.Create().ToRule()),
                RuleTestDefinitions.Catalog, "", 1), "Bearer user-token", cancellation.Token));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("error")]
    public async Task Stream_transport_stops_at_the_first_terminal_event_and_preserves_structured_progress(string terminal)
    {
        var progress = JsonSerializer.SerializeToElement(new
        {
            message = "Checking exact quotes", stage = "verification", state = "started", stepId = "verification-1", mode = "question"
        });
        var payload = terminal == "result" ? new FakePipelineClient().Result : JsonSerializer.SerializeToElement(new { message = "Service failed" });
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: progress\ndata: {progress.GetRawText()}\n\nevent: {terminal}\ndata: {payload.GetRawText()}\n\n"
                + "event: progress\ndata: INVALID_LATE_JSON\n\n", Encoding.UTF8, "text/event-stream")
        }));
        var events = new List<PipelineStreamEvent>();
        await foreach (var item in client.StreamAnalyzeAsync(Request(), "Bearer user-token")) events.Add(item);
        Assert.Equal(new[] { "progress", terminal }, events.Select(e => e.Event));
        Assert.Equal(progress.GetRawText(), events[0].Data.GetRawText());
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Stream_transport_rejects_non_sse_content_before_reading_it_as_progress(string contentType)
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("PRIVATE_UPSTREAM_BODY", Encoding.UTF8, contentType)
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var item in client.StreamAnalyzeAsync(Request(), "Bearer user-token")) { }
        });
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_transport_rejects_malformed_or_oversized_events(bool oversized)
    {
        var data = oversized ? new string('x', 2 * 1024 * 1024 + 1) : "not-json";
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: progress\ndata: {data}\n\n", Encoding.UTF8, "text/event-stream")
        }));
        async Task Read() { await foreach (var item in client.StreamAnalyzeAsync(Request(), "Bearer user-token")) { } }
        if (oversized) await Assert.ThrowsAsync<InvalidDataException>(Read);
        else await Assert.ThrowsAnyAsync<JsonException>(Read);
    }

    [Fact]
    public async Task Stream_cancellation_reaches_pending_body_reads_after_headers_and_disposes_the_response()
    {
        var body = new PendingBodyStream("event: progress\ndata: {\"message\":\"Checking quotes\"}\n\n");
        var content = new StreamContent(body); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var cancellation = new CancellationTokenSource();
        await using var events = client.StreamAnalyzeAsync(Request(), "Bearer user-token", cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("progress", events.Current.Event);
        var pending = events.MoveNextAsync().AsTask();
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body.ReadToken.IsCancellationRequested); Assert.True(body.Disposed);
    }
}
