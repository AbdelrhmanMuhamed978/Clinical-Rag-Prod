using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Services;

/// <summary>
/// Expected-output contract for a test case. Every field is optional; an absent field imposes no constraint.
/// The schema is intentionally small and stable so clinicians can author expectations without seeing case text.
/// </summary>
public sealed class TestExpectation
{
    public List<string> MustHaveProblems { get; set; } = [];
    public List<string> MustNotHaveProblems { get; set; } = [];
    public List<string> MustFlag { get; set; } = [];
    public List<string> MustFireRules { get; set; } = [];
    public int? MinVerifiedItemsPerProblem { get; set; }
    public bool? RequireDraftWarning { get; set; }
    public List<string> ForbiddenPhrases { get; set; } = [];
    public bool? NoUnverifiedShown { get; set; }

    public static TestExpectation Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TestExpectation();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new TestExpectation
            {
                MustHaveProblems = StringArray(root, "mustHaveProblems"),
                MustNotHaveProblems = StringArray(root, "mustNotHaveProblems"),
                MustFlag = StringArray(root, "mustFlag"),
                MustFireRules = StringArray(root, "mustFireRules"),
                MinVerifiedItemsPerProblem = root.TryGetProperty("minVerifiedItemsPerProblem", out var m) && m.ValueKind == JsonValueKind.Number
                    ? m.GetInt32() : null,
                RequireDraftWarning = root.TryGetProperty("requireDraftWarning", out var d) && (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False)
                    ? d.ValueKind == JsonValueKind.True : null,
                ForbiddenPhrases = StringArray(root, "forbiddenPhrases"),
                NoUnverifiedShown = root.TryGetProperty("noUnverifiedShown", out var n) && (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False)
                    ? n.ValueKind == JsonValueKind.True : null,
            };
        }
        catch (JsonException)
        {
            return new TestExpectation(); // Invalid expectations impose no constraints; the diff reports the failure.
        }
    }

    private static List<string> StringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : [];
}

/// <summary>Comparison result between a pipeline result and a test expectation.</summary>
public sealed class TestComparison
{
    public bool Passed { get; set; }
    public List<string> Diffs { get; set; } = [];
    public JsonElement? Result { get; set; }
}

/// <summary>Compares a verified pipeline result against a test expectation. Never returns case text or prompts in diffs.</summary>
public static class TestExpectationComparer
{
    public static TestComparison Compare(JsonElement result, TestExpectation expectation)
    {
        var diffs = new List<string>();
        var passed = true;

        var problems = result.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().ToList() : [];

        // mustHaveProblems: each substring must match at least one problem title.
        foreach (var wanted in expectation.MustHaveProblems)
        {
            if (!problems.Any(pr => pr.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString()!.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add($"Missing expected problem matching “{wanted}”.");
                passed = false;
            }
        }

        // mustNotHaveProblems: no problem title may contain any substring.
        foreach (var unwanted in expectation.MustNotHaveProblems)
        {
            if (problems.Any(pr => pr.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString()!.Contains(unwanted, StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add($"Unexpected problem matching “{unwanted}”.");
                passed = false;
            }
        }

        // minVerifiedItemsPerProblem: every problem with status "ok" must have at least N items.
        if (expectation.MinVerifiedItemsPerProblem is int minItems)
        {
            foreach (var pr in problems)
            {
                var status = pr.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : "";
                if (status == "ok")
                {
                    var items = pr.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                        ? it.EnumerateArray().Count() : 0;
                    if (items < minItems)
                    {
                        diffs.Add($"Problem “{GetTitle(pr)}” has {items} verified item(s), fewer than the required {minItems}.");
                        passed = false;
                    }
                }
            }
        }

        // requireDraftWarning: at least one warning must mention DRAFT.
        if (expectation.RequireDraftWarning == true)
        {
            var warnings = result.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
            if (!warnings.Any(warn => warn.Contains("DRAFT", StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add("Expected a DRAFT guideline warning in the result warnings.");
                passed = false;
            }
        }

        // noUnverifiedShown: no problem may have unverified items.
        if (expectation.NoUnverifiedShown == true)
        {
            foreach (var pr in problems)
            {
                var unverified = pr.TryGetProperty("unverified", out var u) && u.ValueKind == JsonValueKind.Array
                    ? u.EnumerateArray().Count() : 0;
                if (unverified > 0)
                {
                    diffs.Add($"Problem “{GetTitle(pr)}” shows {unverified} unverified item(s).");
                    passed = false;
                }
            }
        }

        // forbiddenPhrases: no phrase may appear in any recommendation or quote text.
        if (expectation.ForbiddenPhrases.Count > 0)
        {
            var text = result.GetRawText();
            foreach (var phrase in expectation.ForbiddenPhrases)
            {
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    diffs.Add($"Forbidden phrase “{phrase}” appears in the result.");
                    passed = false;
                }
            }
        }

        return new TestComparison { Passed = passed, Diffs = diffs, Result = result };

        static string GetTitle(JsonElement problem) =>
            problem.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "(untitled)";
    }
}

/// <summary>Runs test cases through the verified pipeline and persists results.</summary>
public sealed class TestHarnessService
{
    private readonly AppDbContext _db;
    private readonly IPipelineClient _pipeline;
    private readonly IConfigService _config;
    private readonly RuleService _rules;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor? _context;

    public TestHarnessService(AppDbContext db, IPipelineClient pipeline, IConfigService config,
        RuleService rules, IConfiguration configuration, IHttpContextAccessor? context = null)
    {
        _db = db; _pipeline = pipeline; _config = config; _rules = rules;
        _configuration = configuration; _context = context;
    }

    public async Task<List<TestRunResult>> RunAsync(Guid workspaceId, List<Guid>? caseIds, Guid? configId,
        Guid actor, CancellationToken token = default)
    {
        var cases = await _db.TestCases.Where(t => t.WorkspaceId == workspaceId
            && (caseIds == null || caseIds.Contains(t.Id))).ToListAsync(token);
        if (cases.Count == 0) return [];

        // Resolve the config: draft by id, otherwise published.
        ConfigVersion? config = null;
        if (configId != null)
            config = await _db.ConfigVersions.FirstOrDefaultAsync(c => c.Id == configId && c.WorkspaceId == workspaceId, token);
        config ??= await _config.GetPublishedAsync(workspaceId);
        var configJson = JsonSerializer.Deserialize<JsonElement>(config.ConfigJson);
        var configHash = ComputeHash(config.ConfigJson);

        // Approved rules and catalog for this workspace.
        var snapshot = await _rules.ApprovedSnapshotAsync(workspaceId, token);
        var retrieveUrl = BuildRetrieveUrl(workspaceId);
        var results = new List<TestRunResult>(cases.Count);

        // Run one or two at a time (single GPU / bounded concurrency).
        var semaphore = new SemaphoreSlim(2, 2);
        async Task<TestRunResult> RunOne(TestCase c)
        {
            await semaphore.WaitAsync(token);
            try { return await RunCaseAsync(c, config, configJson, configHash, snapshot, retrieveUrl, actor, token); }
            finally { semaphore.Release(); }
        }

        foreach (var task in cases.Select(RunOne))
            results.Add(await task);

        return results.OrderBy(r => r.CaseName).ToList();
    }

    private async Task<TestRunResult> RunCaseAsync(TestCase c, ConfigVersion config, JsonElement configJson,
        string configHash, RuleRequestSnapshot snapshot, string retrieveUrl, Guid actor, CancellationToken token)
    {
        var run = new TestRun
        {
            WorkspaceId = c.WorkspaceId, CaseId = c.Id, ConfigId = config.Id,
            ConfigVersion = config.Version, ConfigHash = configHash, Status = "running", StartedAt = DateTime.UtcNow
        };
        _db.TestRuns.Add(run);
        await _db.SaveChangesAsync(token);

        var started = DateTime.UtcNow;
        string? diff = null;
        bool passed = false;
        JsonElement? result = null;

        try
        {
            var request = new PipelineAnalyzeRequest(
                c.InputText, configJson, snapshot.Rules, [], c.WorkspaceId.ToString(),
                retrieveUrl, null, snapshot.DrugCatalog, snapshot.CatalogRevision);
            result = await _pipeline.AnalyzeAsync(request, null, token);
            if (result is JsonElement res)
            {
                var expectation = TestExpectation.Parse(c.ExpectedJson);
                var comparison = TestExpectationComparer.Compare(res, expectation);
                passed = comparison.Passed;
                diff = comparison.Diffs.Count == 0 ? null : string.Join(" ", comparison.Diffs);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diff = $"Run failed: {ex.GetType().Name}.";
        }

        var completed = DateTime.UtcNow;
        run.Status = passed ? "passed" : "failed";
        run.Diff = diff;
        run.DurationMilliseconds = (completed - started).TotalMilliseconds;
        run.CompletedAt = completed;

        // Update the case's last run.
        c.LastRunJson = result?.GetRawText();
        c.LastRunAt = completed;
        await _db.SaveChangesAsync(token);

        return new TestRunResult
        {
            CaseId = c.Id, CaseName = c.Name, Status = run.Status, Diff = diff,
            DurationMilliseconds = run.DurationMilliseconds, CompletedAt = completed
        };
    }

    private static string ComputeHash(string configJson)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(configJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string BuildRetrieveUrl(Guid workspaceId)
    {
        var configuredBase = _configuration["Backend:PublicUrl"]?.TrimEnd('/');
        var baseUrl = !string.IsNullOrWhiteSpace(configuredBase)
            ? configuredBase
            : _context?.HttpContext != null
                ? $"{_context.HttpContext.Request.Scheme}://{_context.HttpContext.Request.Host}{_context.HttpContext.Request.PathBase}"
                : "http://localhost:5100";
        return $"{baseUrl}/api/workspaces/{workspaceId}/analyze/retrieve";
    }
}

/// <summary>Result of running a single test case.</summary>
public sealed class TestRunResult
{
    public Guid CaseId { get; set; }
    public string CaseName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Diff { get; set; }
    public double DurationMilliseconds { get; set; }
    public DateTime CompletedAt { get; set; }
}
