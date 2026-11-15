using System.Net;
using ClinicalRag.Api.Services;
using ClinicalRag.Api.Services.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClinicalRag.Tests;

public class LegacyAiLoggingTests
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Lines.Add(formatter(state, exception)); }
    }

    [Theory]
    [InlineData(true, "http")]
    [InlineData(false, "http")]
    [InlineData(true, "network")]
    [InlineData(false, "network")]
    [InlineData(true, "json")]
    [InlineData(false, "json")]
    public async Task Inactive_provider_failures_do_not_log_or_throw_raw_bodies_credentials_or_sdk_messages(bool gemini, string failure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Ai:Gemini:ApiKey"] = "PRIVATE_KEY", ["Ai:Jina:ApiKey"] = "PRIVATE_KEY" }).Build();
        using var handler = new DocumentHttpHandler((_, _) => failure == "network"
            ? throw new HttpRequestException("PRIVATE_SDK_MESSAGE")
            : Task.FromResult(new HttpResponseMessage(failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            { Content = new StringContent("PRIVATE_UPSTREAM_BODY", System.Text.Encoding.UTF8, "application/json") }));
        var factory = new FakeHttpClientFactory(handler);
        if (gemini)
        {
            var logger = new RecordingLogger<GeminiEmbeddingService>();
            var service = new GeminiEmbeddingService(factory, configuration, logger);
            var exception = await Assert.ThrowsAsync<EmbeddingServiceException>(() => service.EmbedAsync(["PRIVATE_CASE"]));
            Assert.DoesNotContain("PRIVATE", exception.ToString()); Assert.DoesNotContain("PRIVATE", string.Join('\n', logger.Lines));
            Assert.Contains(OperationTelemetry.InputHash("PRIVATE_CASE"), string.Join('\n', logger.Lines));
        }
        else
        {
            var logger = new RecordingLogger<JinaRerankService>();
            var service = new JinaRerankService(factory, configuration, logger);
            var exception = await Assert.ThrowsAsync<RerankException>(() => service.RerankAsync("PRIVATE_CASE", ["PRIVATE_SOURCE"]));
            Assert.DoesNotContain("PRIVATE", exception.ToString()); Assert.DoesNotContain("PRIVATE", string.Join('\n', logger.Lines));
            Assert.Contains(OperationTelemetry.InputHash("PRIVATE_CASE"), string.Join('\n', logger.Lines));
        }
    }
}
