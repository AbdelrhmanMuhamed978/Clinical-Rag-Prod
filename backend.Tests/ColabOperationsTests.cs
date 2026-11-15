using System.Text.Json;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicalRag.Tests;

/// <summary>Operations regressions: Colab connection store, readiness, platform admin, correlation.</summary>
public class ColabOperationsTests
{
    private sealed class StoreContext : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public AppDbContext Db { get; }
        public IConfiguration Configuration { get; }

        public StoreContext()
        {
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            Db = new AppDbContext(options);
            Db.Database.EnsureCreated();
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Colab:HeartbeatStaleSeconds"] = "120",
            }).Build();
        }

        public void Dispose() { Db.Dispose(); _connection.Dispose(); }
    }

    private static ColabConnectionStore CreateStore(StoreContext ctx)
    {
        var keyRing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"key-ring-tests-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("ClinicalRag.Platform")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRing));
        var sp = services.BuildServiceProvider();
        var protection = sp.GetRequiredService<IDataProtectionProvider>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeyRingPath"] = keyRing,
        }).Build();
        var endpoints = new ColabEndpointPolicy(config);
        var clock = TimeProvider.System;
        return new ColabConnectionStore(ctx.Db, protection, endpoints, config, clock);
    }

    [Fact]
    public async Task Register_creates_connection_and_status_shows_it()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        // Initial registration without heartbeat (notebook startup pattern).
        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "synthetic-api-key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None);

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.True(status.Configured);
        Assert.Equal("https://tunnel.trycloudflare.com/", status.BaseUrl);
        Assert.True(status.HasApiKey);
        Assert.Equal(1, status.Revision);
        Assert.False(status.ManualOverride);
        Assert.NotNull(status.UpdatedAt);
        Assert.NotNull(status.LastHeartbeatAt); // Always set on registration

        // Non-admin status must not expose base URL.
        var publicStatus = await store.StatusAsync(admin: false, CancellationToken.None);
        Assert.True(publicStatus.Configured);
        Assert.Null(publicStatus.BaseUrl);
        Assert.True(publicStatus.HasApiKey);
    }

    [Fact]
    public async Task Heartbeat_updates_last_heartbeat_at()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "synthetic-api-key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None);
        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "synthetic-api-key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, CancellationToken.None);

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.NotNull(status.LastHeartbeatAt);
        Assert.False(status.HeartbeatStale);
    }

    [Fact]
    public async Task Manual_override_blocks_notebook_registration()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.ManualAsync("https://manual.example.com/", "synthetic-api-key-12345678", true, 0, CancellationToken.None);

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.True(status.ManualOverride);

        // Heartbeat registration is blocked by manual override.
        await Assert.ThrowsAsync<ColabSettingConflictException>(() =>
            store.RegisterAsync("https://tunnel.trycloudflare.com/", "other-key-12345678", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, CancellationToken.None));
    }

    [Fact]
    public async Task Same_instance_registration_is_not_a_conflict()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "synthetic-api-key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None);
        // Same instance ID, same URL, same key -> not superseded on heartbeat.
        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "synthetic-api-key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, CancellationToken.None);

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.Equal(1, status.Revision); // Revision unchanged for same registration.
    }

    [Fact]
    public async Task Different_instance_is_superseded_on_heartbeat()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "key-one-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None);

        // Different instance ID with heartbeat=true -> superseded.
        await Assert.ThrowsAsync<ColabSettingConflictException>(() =>
            store.RegisterAsync("https://tunnel.trycloudflare.com/", "key-two-12345678", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, CancellationToken.None));
    }

    [Fact]
    public async Task Manual_setting_increments_revision_and_clears_instance()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.RegisterAsync("https://tunnel.trycloudflare.com/", "key-one-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None);
        await store.ManualAsync("https://manual.example.com/", "key-manual-12345678", true, 1, CancellationToken.None);

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.Equal(2, status.Revision);
        Assert.True(status.ManualOverride);
        Assert.Null(status.LastHeartbeatAt);
    }

    [Fact]
    public async Task Revision_conflict_on_concurrent_manual_update()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await store.ManualAsync("https://manual.example.com/", "key-one-12345678", false, 0, CancellationToken.None);

        // Wrong expected revision -> conflict.
        await Assert.ThrowsAsync<ColabSettingConflictException>(() =>
            store.ManualAsync("https://manual.example.com/", "key-two-12345678", false, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_store_returns_unavailable_when_no_connection()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => store.GetAsync(CancellationToken.None));

        var status = await store.StatusAsync(admin: true, CancellationToken.None);
        Assert.False(status.Configured);
        Assert.Null(status.BaseUrl);
        Assert.False(status.HasApiKey);
    }

    [Fact]
    public async Task Invalid_base_url_is_rejected()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RegisterAsync("http://insecure.example.com/", "key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RegisterAsync("https://localhost/path", "key-12345678", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_api_key_is_rejected()
    {
        using var ctx = new StoreContext();
        var store = CreateStore(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RegisterAsync("https://example.com/", "short", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, CancellationToken.None));
    }

    [Fact]
    public async Task Platform_admin_requires_admin_uid()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Firebase:ProjectId"] = "test-project",
            ["Operations:AdminFirebaseUids:0"] = "admin-uid",
        }).Build();

        // Non-admin UID.
        var nonAdmin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-uid"),
            new System.Security.Claims.Claim("email_verified", "true"),
        }, "Test"));
        Assert.False(PlatformAdmin.IsAllowed(nonAdmin, config));

        // Admin UID.
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-uid"),
            new System.Security.Claims.Claim("email_verified", "true"),
        }, "Test"));
        Assert.True(PlatformAdmin.IsAllowed(admin, config));

        // Unverified email -> not admin even with matching UID.
        var unverified = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-uid"),
            new System.Security.Claims.Claim("email_verified", "false"),
        }, "Test"));
        Assert.False(PlatformAdmin.IsAllowed(unverified, config));

        // No Firebase project -> not admin.
        var noProjConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:AdminFirebaseUids:0"] = "admin-uid",
        }).Build();
        Assert.False(PlatformAdmin.IsAllowed(admin, noProjConfig));
    }

    [Fact]
    public async Task Correlation_accepts_only_32_hex_characters()
    {
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", RequestCorrelation.Create("deadbeefDEADBEEFdeadbeefdeadbeef"));
        Assert.NotEqual("deadbeefdeadbeefdeadbeefdeadbeef", RequestCorrelation.Create("not-hex"));
        Assert.NotEqual("deadbeefdeadbeefdeadbeefdeadbeef", RequestCorrelation.Create("too-short"));
        Assert.NotEqual("deadbeefdeadbeefdeadbeefdeadbeef", RequestCorrelation.Create("way-too-long-33-chars-hexadecimal-string"));
    }

    [Fact]
    public async Task Service_secrets_uses_constant_time_comparison()
    {
        Assert.True(ServiceSecrets.Matches("expected", "expected"));
        Assert.False(ServiceSecrets.Matches("expected", "different"));
        Assert.False(ServiceSecrets.Matches(null, "actual"));
        Assert.False(ServiceSecrets.Matches("expected", null));
    }
}
