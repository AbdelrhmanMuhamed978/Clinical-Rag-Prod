using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data;
using System.Diagnostics;
using System.Globalization;
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
        if (json == null) return new TestExpectation();
        if (json.Length > 32768) throw new TestExpectationException();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TestExpectationException();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new TestExpectationException();
                var valid = property.Name switch
                {
                    "mustHaveProblems" or "mustNotHaveProblems" or "mustFlag" or "mustFireRules" or "forbiddenPhrases" =>
                        property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 100
                        && property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(item.GetString()) && item.GetString()!.Length <= 512),
                    "minVerifiedItemsPerProblem" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var number) && number is >= 0 and <= 100,
                    "requireDraftWarning" or "noUnverifiedShown" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => false
                };
                if (!valid) throw new TestExpectationException();
            }
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
            throw new TestExpectationException();
        }
    }

    private static List<string> StringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : [];
}

public sealed class TestExpectationException : ArgumentException
{
    public TestExpectationException() : base("Invalid ExpectedJson. Use only supported expectation keys with valid types and bounded values.") { }
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
        foreach (var (wanted, index) in expectation.MustHaveProblems.Select((value, index) => (value, index)))
        {
            if (!problems.Any(pr => pr.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString()!.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add($"Missing expected problem (mustHaveProblems #{index + 1}).");
                passed = false;
            }
        }

        // mustNotHaveProblems: no problem title may contain any substring.
        foreach (var (unwanted, index) in expectation.MustNotHaveProblems.Select((value, index) => (value, index)))
        {
            if (problems.Any(pr => pr.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString()!.Contains(unwanted, StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add($"Unexpected problem matching mustNotHaveProblems #{index + 1}.");
                passed = false;
            }
        }

        // minVerifiedItemsPerProblem: every problem with status "ok" must have at least N items.
        if (expectation.MinVerifiedItemsPerProblem is int minItems)
        {
            foreach (var (pr, index) in problems.Select((value, index) => (value, index)))
            {
                var status = pr.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : "";
                if (status == "ok")
                {
                    var items = pr.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                        ? it.EnumerateArray().Count() : 0;
                    if (items < minItems)
                    {
                        diffs.Add($"Problem #{index + 1} has {items} verified item(s), fewer than the required {minItems}.");
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
            foreach (var (pr, index) in problems.Select((value, index) => (value, index)))
            {
                var unverified = pr.TryGetProperty("unverified", out var u) && u.ValueKind == JsonValueKind.Array
                    ? u.EnumerateArray().Count() : 0;
                if (unverified > 0)
                {
                    diffs.Add($"Problem #{index + 1} shows {unverified} unverified item(s).");
                    passed = false;
                }
            }
        }

        // Compare decoded projected text, so JSON escaping cannot hide forbidden wording.
        if (expectation.ForbiddenPhrases.Count > 0)
        {
            var text = TextValues(result).ToList();
            foreach (var (phrase, index) in expectation.ForbiddenPhrases.Select((value, index) => (value, index)))
            {
                if (text.Any(value => value.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    diffs.Add($"Forbidden phrase #{index + 1} appears in the result.");
                    passed = false;
                }
            }
        }

        // Only projected patient-note IDs prove a flag/rule was observed in the result.
        // Do not trust raw facts/plan fields that the privacy policy intentionally excludes.
        var observed = problems.SelectMany(pr => pr.GetProperty("items").EnumerateArray())
            .Where(item => item.TryGetProperty("patient_notes", out _))
            .SelectMany(item => item.GetProperty("patient_notes").EnumerateArray())
            .Select(note => note.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, index) in expectation.MustFlag.Select((value, index) => (value, index)))
            if (!observed.Contains(id)) { diffs.Add($"Expected flag #{index + 1} was not observed in verified patient notes."); passed = false; }
        foreach (var (id, index) in expectation.MustFireRules.Select((value, index) => (value, index)))
            if (!observed.Contains(id)) { diffs.Add($"Expected rule #{index + 1} was not observed in verified patient notes."); passed = false; }
        return new TestComparison { Passed = passed, Diffs = diffs };
    }

    private static IEnumerable<string> TextValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                foreach (var text in TextValues(property.Value)) yield return text;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var text in TextValues(item)) yield return text;
        }
    }
}

/// <summary>Runs test cases through the verified pipeline and persists results.</summary>
public sealed class TestHarnessService
{
    public const int MaxCasesPerRequest = 10;
    public const int MaxDurationSeconds = 240;
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
        Guid actor, string? authorization = null, CancellationToken token = default)
    {
        if (caseIds?.Count > MaxCasesPerRequest) throw new TestRunRequestException("too_many_cases", $"Run at most {MaxCasesPerRequest} cases per request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var seconds = Math.Clamp(_configuration.GetValue("Tests:MaxDurationSeconds", MaxDurationSeconds), 1, MaxDurationSeconds);
        // exp comes from the already authenticated principal, never a client body or a stored token.
        var expiry = _context?.HttpContext?.User.FindFirst("exp")?.Value;
        var duration = TimeSpan.FromSeconds(seconds);
        if (long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
        {
            var remaining = DateTimeOffset.FromUnixTimeSeconds(exp) - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15);
            if (remaining <= TimeSpan.Zero) throw new TestRunRequestException("auth", "Session is too close to expiry. Refresh your session before running tests.");
            if (remaining < duration) duration = remaining;
        }
        // Reserve a single request-wide cleanup budget, not five seconds per case.
        using var cleanup = new CancellationTokenSource(duration);
        deadline.CancelAfter(duration > TimeSpan.FromSeconds(5) ? duration - TimeSpan.FromSeconds(5) : duration * 0.75);
        var runToken = deadline.Token;
        // Bootstrap only when no explicit version was requested; a bad ID never falls back to published.
        if (configId == null) configId = (await _config.GetPublishedAsync(workspaceId, runToken)).Id;
        List<TestCase> cases;
        ConfigVersion config;
        List<TestRun> runs;
        await using (var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, runToken) : null)
        {
            config = await _db.ConfigVersions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == configId && c.WorkspaceId == workspaceId, runToken)
                ?? throw new TestRunRequestException("config_not_found", "Requested test configuration was not found.");
            if (config.Status is not ("draft" or "published")) throw new TestRunRequestException("invalid_config", "Tests require a draft or published configuration.");
            cases = await _db.TestCases.AsNoTracking().Where(t => t.WorkspaceId == workspaceId
                && (caseIds == null || caseIds.Contains(t.Id))).OrderBy(t => t.Id).Take(MaxCasesPerRequest + 1).ToListAsync(runToken);
            if (cases.Count > MaxCasesPerRequest) throw new TestRunRequestException("too_many_cases", $"Run at most {MaxCasesPerRequest} cases per request. Select a bounded subset.");
            if (caseIds != null && cases.Count != caseIds.Distinct().Count()) throw new TestRunRequestException("case_not_found", "One or more requested test cases were not found.");
            var hash = ConfigValidator.Fingerprint(config.ConfigJson);
            // Reserve ALL running rows atomically before invoking the pipeline. Newer work immediately
            // invalidates old passing evidence, and revision capture cannot race with an edit.
            runs = cases.Select(c => new TestRun { WorkspaceId = workspaceId, CaseId = c.Id, ConfigId = config.Id,
                ConfigVersion = config.Version, ConfigHash = hash, CaseRevision = c.Revision, StartedAt = DateTime.UtcNow }).ToList();
            _db.TestRuns.AddRange(runs);
            await _db.SaveChangesAsync(runToken);
            if (transaction != null) await transaction.CommitAsync(runToken);
        }
        var results = new List<TestRunResult>();
        for (var index = 0; index < cases.Count; index++)
            results.Add(await RunCaseAsync(cases[index], config, runs[index], authorization, runToken, cleanup.Token));
        token.ThrowIfCancellationRequested();
        return results.OrderBy(r => r.CaseName).ToList();
    }

    private async Task<TestRunResult> RunCaseAsync(TestCase c, ConfigVersion config, TestRun run, string? authorization, CancellationToken token, CancellationToken cleanupToken)
    {
        var started = Stopwatch.GetTimestamp();
        string? diff = null;
        bool passed = false;
        string? failureCode = null;
        JsonElement? diagnostics = null;

        try
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(authorization)) throw new DependencyUnavailableException("pipeline", failureCode: "auth");
            var snapshot = await _rules.ApprovedSnapshotAsync(c.WorkspaceId, token);
            var request = new PipelineAnalyzeRequest(
                c.InputText, JsonSerializer.Deserialize<JsonElement>(config.ConfigJson), snapshot.Rules, [], c.WorkspaceId.ToString(),
                BuildRetrieveUrl(c.WorkspaceId), null, snapshot.DrugCatalog, snapshot.CatalogRevision, TestRunDiagnostics: true);
            var raw = await _pipeline.AnalyzeAsync(request, authorization, token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var res = PipelineResultPolicy.ForResponse(raw);
            diagnostics = PipelineAnswerDiagnostics.ForTestRun(raw);
            if (res.GetProperty("problems").EnumerateArray().Any(p => p.GetProperty("status").GetString() == "error"))
                throw new DependencyUnavailableException("pipeline");
            var comparison = TestExpectationComparer.Compare(res, TestExpectation.Parse(c.ExpectedJson));
            passed = comparison.Passed;
            failureCode = passed ? null : "comparison";
            diff = comparison.Diffs.Count == 0 ? null : string.Join(" ", comparison.Diffs);
        }
        catch (Exception ex)
        {
            failureCode = ex switch
            {
                OperationCanceledException or TimeoutException => "timeout",
                DependencyUnavailableException unavailable => unavailable.FailureCode,
                HttpRequestException http when http.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "auth",
                HttpRequestException http when http.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.GatewayTimeout => "timeout",
                InvalidDataException or JsonException or TestExpectationException => "invalid_result",
                _ => "pipeline_unavailable"
            };
            diff = failureCode switch
            {
                "auth" => "Run failed: pipeline authentication was rejected. Refresh your session and retry.",
                "timeout" => "Run failed: the test execution deadline was reached. Retry a smaller batch.",
                "invalid_result" => "Run failed: the pipeline result or stored expectation was invalid. No passing evidence was recorded.",
                _ => "Run failed: the verified pipeline is unavailable. Retry when it is ready."
            };
        }

        var completed = DateTime.UtcNow;
        run.Status = passed ? "passed" : "failed";
        run.Diff = diff;
        run.FailureCode = failureCode;
        run.DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        run.CompletedAt = completed;

        // Update the case's last run.
        // Completion uses a small independent cleanup deadline even when the caller disconnects.
        // Keep raw pipeline output solely in memory. Never overwrite an edited case snapshot.
        await _db.SaveChangesAsync(cleanupToken);
        if (_db.Database.IsRelational())
            await _db.TestCases.Where(item => item.Id == c.Id && item.Revision == c.Revision)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.LastRunJson, (string?)null)
                    .SetProperty(item => item.LastRunAt, completed), cleanupToken);
        else
        {
            var current = await _db.TestCases.FirstOrDefaultAsync(item => item.Id == c.Id, cleanupToken);
            if (current != null && current.Revision == c.Revision)
            {
                current.LastRunJson = null; current.LastRunAt = completed;
                try { await _db.SaveChangesAsync(cleanupToken); }
                catch (DbUpdateConcurrencyException) { _db.Entry(current).State = EntityState.Detached; }
            }
        }

        return new TestRunResult
        {
            Id = run.Id, CaseId = c.Id, CaseName = c.Name, Status = run.Status, Diff = diff, FailureCode = failureCode,
            DurationMilliseconds = run.DurationMilliseconds, CompletedAt = completed, Diagnostics = diagnostics
        };
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
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string CaseName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Diff { get; set; }
    public string? FailureCode { get; set; }
    public double DurationMilliseconds { get; set; }
    public DateTime CompletedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Diagnostics { get; set; }
}

public sealed class TestRunRequestException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
