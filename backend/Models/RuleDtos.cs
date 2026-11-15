using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ClinicalRag.Api.Models;

// Only editable definition fields are bound. IDs, approval and audit identity are server-owned.
public sealed record CreateRuleDto(
    [Required, StringLength(64)] string RuleId,
    [Required, StringLength(256)] string Title,
    string? DrugClasses = null,
    string? AppliesWhen = null,
    [StringLength(64)] string? Level = null,
    [StringLength(10000)] string? Message = null,
    [StringLength(10000)] string? Caveat = null,
    string? Sources = null,
    [StringLength(512)] string? LinkTerms = null,
    bool CaseLevel = false,
    bool Enabled = true)
{
    public Rule ToRule() => new()
    {
        RuleId = RuleId, Title = Title, DrugClasses = DrugClasses, AppliesWhen = AppliesWhen,
        Level = Level, Message = Message, Caveat = Caveat, Sources = Sources, LinkTerms = LinkTerms,
        CaseLevel = CaseLevel, Enabled = Enabled
    };
}

/// <summary>Null/omitted values are no-ops, including CaseLevel. Empty strings explicitly clear optional text.</summary>
public sealed record UpdateRuleDto(
    [StringLength(256, MinimumLength = 1)] string? Title = null,
    string? DrugClasses = null,
    string? AppliesWhen = null,
    [StringLength(64)] string? Level = null,
    [StringLength(10000)] string? Message = null,
    [StringLength(10000)] string? Caveat = null,
    string? Sources = null,
    [StringLength(512)] string? LinkTerms = null,
    bool? CaseLevel = null,
    [StringLength(64, MinimumLength = 1)] string? RuleId = null,
    [Range(1, int.MaxValue)] int? ExpectedRevision = null);

public sealed record RuleRevisionDto([Range(1, int.MaxValue)] int? ExpectedRevision = null);
public sealed record RuleEnablementDto(bool Enabled, [Range(1, int.MaxValue)] int? ExpectedRevision = null);
public sealed record UpdateDrugCatalogDto(JsonElement Catalog, [Range(0, int.MaxValue)] int? ExpectedRevision = null);
public sealed record RulePreviewDto(
    [Required] CreateRuleDto Rule,
    [StringLength(100000)] string Text = "",
    [StringLength(10000)] string RegexSample = "",
    [Range(0, int.MaxValue)] int? ExpectedCatalogRevision = null);
