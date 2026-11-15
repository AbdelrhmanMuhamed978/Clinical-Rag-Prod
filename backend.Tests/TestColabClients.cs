using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ClinicalRag.Tests;

/// <summary>Explicit synthetic connection seam; production never falls back to appsettings URLs or keys.</summary>
internal sealed class TestColabConnectionProvider(IConfiguration configuration) : IColabConnectionProvider
{
    public Task<ColabConnection> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ColabConnection(new Uri(configuration["Colab:RetrieveUrl"] ?? "http://colab.test/"),
            configuration["Colab:ApiKey"] ?? "synthetic-colab-key", 1));
    }
}

internal static class TestColabClients
{
    public static ColabClient Create(IConfiguration configuration, IHttpClientFactory factory, IHttpContextAccessor? context = null) =>
        new(factory.CreateClient(), new TestColabConnectionProvider(configuration), configuration, context);
}
