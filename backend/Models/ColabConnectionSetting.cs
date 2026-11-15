using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ClinicalRag.Api.Models;

/// <summary>One platform setting, never a workspace-owned setting or browser DTO.</summary>
public sealed class ColabConnectionSetting
{
    public int Id { get; set; } = 1;
    [MaxLength(2048)] public string BaseUrl { get; set; } = "";
    [MaxLength(8192), JsonIgnore] public string ProtectedApiKey { get; set; } = "";
    public int Revision { get; set; } = 1;
    public bool ManualOverride { get; set; }
    [MaxLength(32)] public string? InstanceId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
}
