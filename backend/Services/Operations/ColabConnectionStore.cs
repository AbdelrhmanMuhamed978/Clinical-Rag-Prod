using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

public sealed class ColabConnection(Uri baseUri, string apiKey, int revision)
{
    [JsonIgnore] public Uri BaseUri { get; } = baseUri;
    [JsonIgnore] public string ApiKey { get; } = apiKey;
    public int Revision { get; } = revision;
    [JsonIgnore] public string CircuitKey { get; } = OperationTelemetry.InputHash(baseUri.AbsoluteUri + ":" + OperationTelemetry.InputHash(apiKey));
    public override string ToString() => "Colab connection (redacted)";
}

public interface IColabConnectionProvider
{
    Task<ColabConnection> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record ColabConnectionStatus(bool Configured, string? BaseUrl, bool HasApiKey, int Revision,
    bool ManualOverride, DateTimeOffset? UpdatedAt, DateTimeOffset? LastHeartbeatAt, bool HeartbeatStale);
public sealed class ColabSettingConflictException(string code) : Exception("The Colab setting changed. Refresh and retry.")
{
    public string Code { get; } = code;
}

/// <summary>Read fresh for every logical operation. No process-local URL/key cache survives rotation.</summary>
public sealed class ColabConnectionStore(AppDbContext db, IDataProtectionProvider protection, ColabEndpointPolicy endpoints,
    IConfiguration configuration, TimeProvider clock) : IColabConnectionProvider
{
    private readonly IDataProtector _protector = protection.CreateProtector("ClinicalRag.ColabConnection.ApiKey.v1");

    public async Task<ColabConnection> GetAsync(CancellationToken cancellationToken = default)
    {
        var setting = await db.ColabConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == 1, cancellationToken);
        if (setting == null) throw new DependencyUnavailableException("colab");
        try { return new(endpoints.Validate(setting.BaseUrl), _protector.Unprotect(setting.ProtectedApiKey), setting.Revision); }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        { throw new DependencyUnavailableException("colab"); }
    }

    public async Task<ColabConnectionStatus> StatusAsync(bool admin, CancellationToken cancellationToken = default)
    {
        var row = await db.ColabConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == 1, cancellationToken);
        var staleAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Colab:HeartbeatStaleSeconds", 120), 30, 3600));
        return row == null ? new(false, null, false, 0, false, null, null, true)
            : new(true, admin ? row.BaseUrl : null, row.ProtectedApiKey.Length > 0, row.Revision, row.ManualOverride,
                row.UpdatedAt, row.LastHeartbeatAt, row.LastHeartbeatAt == null || clock.GetUtcNow() - row.LastHeartbeatAt > staleAfter);
    }

    public async Task RegisterAsync(string baseUrl, string apiKey, string instanceId, bool heartbeat, CancellationToken cancellationToken)
    {
        var uri = endpoints.Validate(baseUrl);
        ValidateKey(apiKey);
        if (instanceId.Length != 32 || !instanceId.All(Uri.IsHexDigit)) throw new ArgumentException("Use a 32-character hexadecimal runtime instance ID.");
        instanceId = instanceId.ToLowerInvariant();
        var row = await db.ColabConnections.SingleOrDefaultAsync(c => c.Id == 1, cancellationToken);
        if (row?.ManualOverride == true) throw new ColabSettingConflictException("manual_override");
        var same = row != null && row.InstanceId == instanceId && row.BaseUrl == uri.AbsoluteUri
            && KeyMatches(row.ProtectedApiKey, apiKey);
        if (heartbeat && !same) throw new ColabSettingConflictException("superseded");
        if (row == null) { row = new(); db.ColabConnections.Add(row); }
        else if (!same) row.Revision = checked(row.Revision + 1);
        if (!same)
        {
            row.BaseUrl = uri.AbsoluteUri;
            row.ProtectedApiKey = _protector.Protect(apiKey);
            row.InstanceId = instanceId;
            row.UpdatedAt = clock.GetUtcNow();
        }
        row.LastHeartbeatAt = clock.GetUtcNow();
        await SaveAsync(cancellationToken);
    }

    public async Task ManualAsync(string baseUrl, string? apiKey, bool manualOverride, int expectedRevision, CancellationToken cancellationToken)
    {
        var uri = endpoints.Validate(baseUrl);
        var row = await db.ColabConnections.SingleOrDefaultAsync(c => c.Id == 1, cancellationToken);
        if ((row?.Revision ?? 0) != expectedRevision) throw new ColabSettingConflictException("revision_conflict");
        if (row == null && string.IsNullOrEmpty(apiKey)) throw new ArgumentException("An API key is required for the first connection.");
        if (!string.IsNullOrEmpty(apiKey)) ValidateKey(apiKey);
        if (row == null) { row = new(); db.ColabConnections.Add(row); }
        else row.Revision = checked(row.Revision + 1);
        row.BaseUrl = uri.AbsoluteUri;
        if (!string.IsNullOrEmpty(apiKey)) row.ProtectedApiKey = _protector.Protect(apiKey);
        row.ManualOverride = manualOverride;
        row.InstanceId = null;
        row.LastHeartbeatAt = null;
        row.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(cancellationToken);
    }

    private bool KeyMatches(string protectedKey, string key)
    {
        try { return ServiceSecrets.Matches(_protector.Unprotect(protectedKey), key); }
        catch (CryptographicException) { return false; }
    }
    private static void ValidateKey(string key)
    {
        if (key.Length is < 16 or > 4096 || key.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Use an API key of 16–4096 printable ASCII characters without spaces.");
    }
    private async Task SaveAsync(CancellationToken token)
    {
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new ColabSettingConflictException("revision_conflict"); }
        catch (DbUpdateException exception) when (db.ColabConnections.Local.Any(row => db.Entry(row).State == EntityState.Added)
            && UniqueViolation(exception.InnerException))
        { throw new ColabSettingConflictException("revision_conflict"); }
    }

    private static bool UniqueViolation(Exception? exception) => exception is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }
        || exception?.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException"
            && exception.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(exception) is 1555 or 2067;
}
