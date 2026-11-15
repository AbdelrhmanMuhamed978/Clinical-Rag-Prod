using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ClinicalRag.Api.Services;

namespace ClinicalRag.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use EF InMemory for tests (Program.cs selects InMemory when this flag is true).
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClinicalRag:UseInMemory"] = "true",
                // Each test owns its seeded state. Worker behavior is exercised
                // explicitly rather than processing another suite's fixtures.
                ["Documents:AutomaticParsing"] = "false",
                ["Documents:AutomaticFileCleanup"] = "false",
                ["Colab:TimeoutSeconds"] = "30",
                ["Colab:ChunkJobPollMilliseconds"] = "100"
            });
        });

        // Replace Firebase JWT auth with a test scheme driven by TestPrincipal.Current.
        // Configured via ConfigureServices on the host builder so it runs before Program builds the app.
        builder.ConfigureServices(services =>
        {
            services.AddScoped<IColabConnectionProvider, TestColabConnectionProvider>();
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultAuthenticateScheme = "Test";
                o.DefaultChallengeScheme = "Test";
            });
        });
    }
}
