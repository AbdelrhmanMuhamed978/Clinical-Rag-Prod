using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinicalRag.Tests;

public sealed class FakePipelineClient : IPipelineClient
{
    public PipelineAnalyzeRequest? Request { get; private set; }
    public string? Authorization { get; private set; }
    public JsonElement Result { get; set; } = JsonSerializer.SerializeToElement(new
    {
        plan_source = "single_question",
        facts = new { summary = "must never be stored" },
        problems = new[]
        {
            new
            {
                title = "Septic shock",
                status = "ok",
                queries = Array.Empty<string>(),
                sources = Array.Empty<object>(),
                items = new[]
                {
                    new
                    {
                        recommendation = "Use norepinephrine.",
                        quote = "we recommend norepinephrine",
                        citation = new { id = "S1", label = "SCCM 2024", section = "Shock", page = "12", strength = "strong", draft = false }
                    }
                },
                unverified = Array.Empty<object>()
            }
        },
        not_covered = Array.Empty<string>(),
        warnings = Array.Empty<string>(),
        disclaimer = "Decision support only.",
        version = "3.2"
    });
    public IReadOnlyList<PipelineStreamEvent>? StreamEvents { get; set; }
    public Action<PipelineStreamEvent>? BeforeStreamEvent { get; set; }
    public int StreamEventsRead { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public Func<CancellationToken, Task<JsonElement>>? AnalyzeHandler { get; set; }

    public Task<JsonElement> AnalyzeAsync(PipelineAnalyzeRequest request, string? authorization,
        CancellationToken cancellationToken = default)
    {
        Request = request;
        Authorization = authorization;
        LastCancellationToken = cancellationToken;
        return AnalyzeHandler?.Invoke(cancellationToken) ?? Task.FromResult(Result);
    }

    public async IAsyncEnumerable<PipelineStreamEvent> StreamAnalyzeAsync(
        PipelineAnalyzeRequest request, string? authorization,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Request = request;
        Authorization = authorization;
        LastCancellationToken = cancellationToken;
        foreach (var item in StreamEvents ?? new[] { new PipelineStreamEvent("result", Result) })
        {
            StreamEventsRead++;
            BeforeStreamEvent?.Invoke(item);
            yield return item;
        }
        await Task.CompletedTask;
    }
}

public sealed class FakeCurrentUser : ICurrentUser
{
    private readonly User _user;
    public FakeCurrentUser(User user) => _user = user;
    public string? FirebaseUid => _user.FirebaseUid;
    public string Email => _user.Email;
    public bool IsAuthenticated => true;
    public Task<User> EnsureUserAsync() => Task.FromResult(_user);
    public Task<Guid> GetUserIdAsync() => Task.FromResult(_user.Id);
}

[Collection("Sequential")]
public class VerifiedPipelineControllerTests
{
    [Fact]
    public async Task Analyze_calls_verified_pipeline_and_writes_privacy_safe_audit_run()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"VerifiedPipeline_{Guid.NewGuid():N}")
            .Options;
        await using var db = new AppDbContext(options);

        var user = db.Users.Add(new User { FirebaseUid = "pipeline-user", Email = "user@example.com" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Clinical", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "owner" });
        db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id,
            Version = 4,
            Status = "published",
            ConfigJson = ConfigService.DefaultConfig()
        });
        db.Rules.Add(new Rule
        {
            WorkspaceId = workspace.Id,
            RuleId = "approved-rule",
            Title = "Approved",
            DrugClasses = "[\"nsaid\"]",
            AppliesWhen = "{\"any\":[{\"field\":\"creatinine\",\"op\":\">=\",\"value\":1.5}]}",
            Sources = "[{\"title\":\"Synthetic source\",\"url\":\"https://example.org/source\"}]",
            Status = "approved", Enabled = true, ReviewedCatalogRevision = 0,
            ReviewedAt = DateTime.UtcNow,
            ReviewedBy = user.Id
        });
        db.Rules.Add(new Rule
        {
            WorkspaceId = workspace.Id,
            RuleId = "unapproved-rule",
            Title = "Unapproved",
            DrugClasses = "[]",
            AppliesWhen = "{\"any\":[]}"
        });
        await db.SaveChangesAsync();

        var pipeline = new FakePipelineClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new AnalysisController(
            db,
            new FakeColabClient(),
            new ConfigService(db),
            pipeline,
            new FakeCurrentUser(user),
            configuration,
            NullLogger<AnalysisController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.Request.Scheme = "http";
        controller.Request.Host = new HostString("backend.test");
        controller.Request.Headers.Authorization = "Bearer forwarded-token";

        var response = await controller.Analyze(
            workspace.Id,
            new AnalyzeDto(
                "Which vasopressor is first line?",
                "conversation-1",
                new[] { new ConversationTurnDto("What about shock?", "Norepinephrine is verified.") }));

        var ok = Assert.IsType<OkObjectResult>(response);
        var returned = JsonSerializer.SerializeToElement(ok.Value);
        Assert.True(returned.TryGetProperty("problems", out _));
        Assert.False(returned.TryGetProperty("answer", out _));

        Assert.NotNull(pipeline.Request);
        Assert.Equal(workspace.Id.ToString(), pipeline.Request.WorkspaceId);
        Assert.Single(pipeline.Request.Rules);
        Assert.Equal("approved-rule", pipeline.Request.Rules[0].Id);
        Assert.Equal("approved", pipeline.Request.Rules[0].Status);
        Assert.True(pipeline.Request.Rules[0].Enabled);
        Assert.True(pipeline.Request.DrugCatalog.TryGetProperty("metformin", out _));
        Assert.Equal(0, pipeline.Request.CatalogRevision);
        Assert.Equal("Bearer forwarded-token", pipeline.Authorization);

        var run = await db.AnalysisRuns.SingleAsync();
        Assert.Equal(user.Id, run.UserId);
        Assert.Equal(4, run.ConfigVersion);
        Assert.NotNull(run.ResultJson); // short questions store results by default
        Assert.DoesNotContain("facts", run.ResultJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must never be stored", run.ResultJson!);
        Assert.DoesNotContain("Which vasopressor", run.ResultJson!);
    }

    private sealed record Setup(AppDbContext Db, User User, Workspace Workspace,
        FakePipelineClient Pipeline, AnalysisController Controller);

    private static async Task<Setup> CreateControllerAsync(string? privacySetting = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PipelineSafety_{Guid.NewGuid():N}").Options);
        var user = db.Users.Add(new User { FirebaseUid = "pipeline-user", Email = "user@example.com" }).Entity;
        var workspace = db.Workspaces.Add(new Workspace { Name = "Clinical", OwnerId = user.Id }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = "editor" });
        db.ConfigVersions.Add(new ConfigVersion
        {
            WorkspaceId = workspace.Id, Version = 7, Status = "published", ConfigJson = ConfigService.DefaultConfig()
        });
        await db.SaveChangesAsync();
        var settings = new Dictionary<string, string?> { ["Backend:PublicUrl"] = "http://backend.test" };
        if (privacySetting != null) settings["Privacy:StoreResults"] = privacySetting;
        var pipeline = new FakePipelineClient();
        var controller = new AnalysisController(db, new FakeColabClient(), new ConfigService(db), pipeline,
            new FakeCurrentUser(user), new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<AnalysisController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer forwarded-user-token";
        controller.Response.Body = new MemoryStream();
        return new Setup(db, user, workspace, pipeline, controller);
    }

    private static PipelineStreamEvent Progress(string mode = "question") => new("progress", JsonSerializer.SerializeToElement(new
    {
        message = "Reading stage evidence", stage = mode == "chat" ? "routing" : mode == "case" ? "facts" : "planning",
        state = mode == "chat" ? "completed" : "started", stepId = "planning", mode,
        patientInput = "PRIVATE_PROGRESS_INPUT", answer = "UNVERIFIED_PROGRESS_ANSWER"
    }));

    private static async Task<List<PipelineStreamEvent>> ReadStreamAsync(Setup setup, string text = "Synthetic guideline question?")
    {
        await setup.Controller.Stream(setup.Workspace.Id, new AnalyzeDto(text, "conversation-1"));
        setup.Controller.Response.Body.Position = 0;
        var body = await new StreamReader(setup.Controller.Response.Body).ReadToEndAsync();
        var events = new List<PipelineStreamEvent>();
        foreach (var record in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n');
            var name = lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            using var json = JsonDocument.Parse(data);
            events.Add(new(name, json.RootElement.Clone()));
        }
        return events;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_analysis_never_requests_or_exposes_injected_diagnostics(bool stream)
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        var raw = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!;
        raw["diagnostics"] = JsonNode.Parse("""
            [{"problemIndex":1,"drafts":1,"candidates":1,"verified":1,
              "quote":"PRIVATE_DIAGNOSTIC_TEXT","reasons":{"quote_not_found":1}}]
            """);
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(raw);
        string returned;
        if (stream)
            returned = JsonSerializer.Serialize(await ReadStreamAsync(setup));
        else
        {
            var response = await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Synthetic source question?"));
            returned = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(response).Value);
        }
        Assert.NotNull(setup.Pipeline.Request);
        Assert.False(setup.Pipeline.Request.TestRunDiagnostics);
        Assert.DoesNotContain("diagnostics", returned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC_TEXT", returned);
        var stored = (await db.AnalysisRuns.SingleAsync()).ResultJson;
        Assert.NotNull(stored);
        Assert.DoesNotContain("diagnostics", stored!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC_TEXT", stored!);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData("false", false, false)]
    [InlineData("true", true, true)]
    [InlineData("invalid", false, false)]
    public async Task Result_storage_defaults_and_overrides_fail_closed(string? setting, bool caseMode, bool shouldStore)
    {
        var setup = await CreateControllerAsync(setting);
        await using var db = setup.Db;
        var text = caseMode ? string.Join(' ', Enumerable.Repeat("synthetic-case", 45)) : "Which vasopressor?";
        Assert.IsType<OkObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto(text)));
        var run = await db.AnalysisRuns.SingleAsync();
        Assert.Equal(shouldStore, run.ResultJson != null);
        Assert.Equal(setup.User.Id, run.UserId);
        Assert.Equal(7, run.ConfigVersion);
        Assert.Equal(64, run.InputHash.Length);
    }

    [Fact]
    public async Task Short_case_facts_keep_the_privacy_storage_rule_strict()
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        Assert.IsType<OkObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id,
            new AnalyzeDto("62 year old woman with BP 80/50 and creatinine 2.5; which vasopressor?")));
        var run = await db.AnalysisRuns.SingleAsync();
        Assert.Null(run.ResultJson);
        Assert.Equal(64, run.InputHash.Length);
    }

    [Fact]
    public async Task Free_text_pipeline_response_is_rejected_and_not_saved()
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(new { answer = "UNVERIFIED_FREE_TEXT" });
        var response = Assert.IsType<ObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Question")));
        Assert.Equal(502, response.StatusCode);
        Assert.Null((await db.AnalysisRuns.SingleAsync()).ResultJson);
    }

    [Theory]
    [InlineData("ok", false)]
    [InlineData("error", true)]
    [InlineData("unverified", true)]
    [InlineData("not_found", true)]
    public async Task Contradictory_verification_status_is_rejected_and_not_saved(string status, bool hasItems)
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        var result = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!;
        result["problems"]![0]!["status"] = status;
        if (!hasItems) result["problems"]![0]!["items"] = new JsonArray();
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(result);

        var response = Assert.IsType<ObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Question")));
        Assert.Equal(502, response.StatusCode);
        Assert.Null((await db.AnalysisRuns.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task Opted_in_case_storage_keeps_guideline_evidence_not_case_details_or_unverified_text()
    {
        var setup = await CreateControllerAsync("true");
        await using var db = setup.Db;
        const string marker = "PRIVATE_CASE_MARKER";
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(new
        {
            plan_source = "rules", version = "3.2", disclaimer = "Decision support only.",
            facts = new { summary = marker }, patientSummary = marker, text = marker,
            problems = new[] { new
            {
                title = marker, status = "ok", queries = new[] { marker }, sources = Array.Empty<object>(),
                patient_summary = marker,
                items = new[] { new
                {
                    recommendation = $"Patient summary: {marker}. Use norepinephrine.",
                    quote = "we recommend norepinephrine",
                    citation = new { id = "S1", label = "SCCM 2024", section = "Shock", page = "12", strength = "strong", draft = false },
                    patient_notes = new[] { new { id = "flag", title = "Review", level = "caution", reasons = new[] { marker } } }
                } },
                unverified = new[] { new { recommendation = marker, quote = marker, issues = new[] { "quote not found" } } }
            } },
            not_covered = Array.Empty<string>(), warnings = new[] { $"Patient-specific fact: {marker}" }
        });
        var text = string.Join(' ', Enumerable.Repeat(marker, 45));
        var response = Assert.IsType<OkObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto(text)));
        var returned = JsonSerializer.SerializeToElement(response.Value);
        Assert.False(returned.TryGetProperty("facts", out _));
        Assert.True(returned.GetProperty("problems")[0].GetProperty("items")[0].TryGetProperty("patient_notes", out _));
        Assert.Contains(marker, returned.GetProperty("warnings")[0].GetString()!);
        var stored = (await db.AnalysisRuns.SingleAsync()).ResultJson!;
        Assert.DoesNotContain(marker, stored);
        Assert.DoesNotContain("patient_notes", stored);
        Assert.Contains("we recommend norepinephrine", stored);
    }

    [Fact]
    public void Case_storage_rebuilds_draft_and_uncovered_warnings_without_patient_text()
    {
        var result = JsonNode.Parse(new FakePipelineClient().Result.GetRawText())!;
        result["warnings"] = new JsonArray("PRIVATE_WARNING_MARKER");
        result["problems"]![0]!["items"]![0]!["citation"]!["draft"] = true;
        var uncovered = result["problems"]![0]!.DeepClone();
        uncovered["status"] = "not_found";
        uncovered["title"] = "PRIVATE_WARNING_MARKER";
        uncovered["items"] = new JsonArray();
        result["problems"]!.AsArray().Add(uncovered);

        var stored = PipelineResultPolicy.ForStorage(JsonSerializer.SerializeToElement(result), caseMode: true);
        var warnings = stored.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("Some citations come from DRAFT guidelines (marked DRAFT). Do not treat them as final.", warnings);
        Assert.Contains("“Not covered” means the loaded guidelines gave no verifiable answer. It does not mean the topic is unimportant or safe.", warnings);
        Assert.DoesNotContain("PRIVATE_WARNING_MARKER", stored.GetRawText());
        Assert.Equal(stored.GetRawText(), PipelineResultPolicy.ForStorage(stored, caseMode: true).GetRawText());
    }

    [Fact]
    public async Task History_is_bounded_and_case_follow_up_does_not_store_results_by_default()
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        var history = Enumerable.Range(0, 8).Select(index => new ConversationTurnDto(
            index == 7 ? string.Join(' ', Enumerable.Repeat("synthetic-case", 45)) : $"Question {index}",
            "Verified statement only")).ToList();
        await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("What about the mother?", "conversation-1", history));
        Assert.Equal(6, setup.Pipeline.Request!.History.Count);
        Assert.Equal("Question 2", setup.Pipeline.Request.History[0].Question);
        Assert.Equal("conversation-1", setup.Pipeline.Request.ConversationId);
        Assert.Null((await db.AnalysisRuns.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task Stream_without_a_terminal_result_reports_an_error()
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        setup.Pipeline.StreamEvents = new[]
        {
            new PipelineStreamEvent("progress", JsonSerializer.SerializeToElement(new { message = "Checking quotes" }))
        };
        await setup.Controller.Stream(setup.Workspace.Id, new AnalyzeDto("Question"));
        setup.Controller.Response.Body.Position = 0;
        var output = await new StreamReader(setup.Controller.Response.Body).ReadToEndAsync();
        Assert.Contains("event: progress", output);
        Assert.Contains("event: error", output);
        Assert.Null((await db.AnalysisRuns.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task Only_current_rules_reviewed_by_a_user_are_forwarded()
    {
        var setup = await CreateControllerAsync();
        await using var db = setup.Db;
        var reviewed = DateTime.UtcNow;
        db.Rules.Add(new Rule { WorkspaceId = setup.Workspace.Id, RuleId = "approved", Title = "Approved",
            DrugClasses = "[\"nsaid\"]", AppliesWhen = "{\"any\":[{\"field\":\"sbp\",\"op\":\"<\",\"value\":90}]}",
            Sources = "[{\"title\":\"Synthetic source\",\"url\":\"https://example.org/source\"}]", Status = "approved", Enabled = true, ReviewedCatalogRevision = 0,
            ReviewedAt = reviewed, ReviewedBy = setup.User.Id, UpdatedAt = reviewed });
        db.Rules.Add(new Rule { WorkspaceId = setup.Workspace.Id, RuleId = "stale", Title = "Edited after review",
            ReviewedAt = reviewed, ReviewedBy = setup.User.Id, UpdatedAt = reviewed.AddSeconds(1) });
        db.Rules.Add(new Rule { WorkspaceId = setup.Workspace.Id, RuleId = "no-reviewer", Title = "No reviewer",
            ReviewedAt = reviewed, UpdatedAt = reviewed });
        await db.SaveChangesAsync();
        await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Question"));
        Assert.Equal(new[] { "approved" }, setup.Pipeline.Request!.Rules.Select(rule => rule.Id));
    }

    [Theory]
    [InlineData("question")]
    [InlineData("case")]
    [InlineData("chat")]
    public async Task Stream_preserves_authoritative_stage_evidence_without_progress_payload_extras(string mode)
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        var result = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!;
        result["plan_source"] = mode == "case" ? "rules" : mode == "chat" ? "chat" : "single_question";
        if (mode == "chat") { result["problems"] = new JsonArray(); result["chat"] = "Ask a guideline question."; }
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(result);
        setup.Pipeline.StreamEvents = [Progress(mode), new("result", setup.Pipeline.Result)];
        var text = mode == "case" ? string.Join(' ', Enumerable.Repeat("synthetic-case", 45)) : mode == "chat" ? "hello" : "Synthetic guideline question?";
        var events = await ReadStreamAsync(setup, text);
        Assert.Equal(new[] { "progress", "result" }, events.Select(e => e.Event));
        var progress = events[0].Data;
        Assert.Equal(new[] { "message", "stage", "state", "stepId", "mode" }, progress.EnumerateObject().Select(p => p.Name));
        Assert.Equal(mode, progress.GetProperty("mode").GetString());
        Assert.Equal(mode == "chat" ? "completed" : "started", progress.GetProperty("state").GetString());
        Assert.DoesNotContain("PRIVATE_", progress.GetRawText()); Assert.DoesNotContain("UNVERIFIED_", progress.GetRawText());
        Assert.Equal("conversation-1", setup.Pipeline.Request!.ConversationId);
        Assert.Equal("no-store", setup.Controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", setup.Controller.Response.Headers["X-Accel-Buffering"].ToString());
    }

    [Fact]
    public async Task Legacy_progress_is_forwarded_without_invented_completed_checklist_metadata()
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        setup.Pipeline.StreamEvents = [new("progress", JsonSerializer.SerializeToElement(new { message = "Legacy reported step", facts = "PRIVATE_FACTS" })),
            new("result", setup.Pipeline.Result)];
        var events = await ReadStreamAsync(setup);
        Assert.Equal("message", Assert.Single(events[0].Data.EnumerateObject()).Name);
        Assert.Equal("Legacy reported step", events[0].Data.GetProperty("message").GetString());
    }

    public static IEnumerable<object[]> InvalidProgress()
    {
        foreach (var json in new[] { "null", "[]", "{}", "{\"message\":\" \"}", "{\"message\":123}",
            "{\"message\":\"PRIVATE_PARTIAL\",\"stage\":\"planning\"}",
            "{\"message\":\"PRIVATE_STATE\",\"stage\":\"planning\",\"state\":\"done\",\"stepId\":\"planning\",\"mode\":\"question\"}",
            "{\"message\":\"PRIVATE_MODE\",\"stage\":\"planning\",\"state\":\"started\",\"stepId\":\"planning\",\"mode\":\"maybe-case\"}",
            "{\"message\":\"PRIVATE_STEP\",\"stage\":\"planning\",\"state\":\"started\",\"stepId\":\"../secret\",\"mode\":\"question\"}",
            "{\"message\":\"PRIVATE_STAGE\",\"stage\":\"raw_answer\",\"state\":\"completed\",\"stepId\":\"planning\",\"mode\":\"question\"}" }) yield return [json];
        yield return [JsonSerializer.Serialize(new { message = new string('x', 501) })];
    }

    [Theory]
    [MemberData(nameof(InvalidProgress))]
    public async Task Invalid_progress_fails_closed_before_echoing_or_saving_any_result(string json)
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        setup.Pipeline.StreamEvents = [new("progress", JsonDocument.Parse(json).RootElement.Clone()), new("result", setup.Pipeline.Result)];
        var events = await ReadStreamAsync(setup);
        Assert.Equal("error", Assert.Single(events).Event); Assert.DoesNotContain("PRIVATE", events[0].Data.GetRawText());
        Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson); Assert.Equal(1, setup.Pipeline.StreamEventsRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contradictory_progress_or_result_modes_end_the_stream_without_persisting_an_answer(bool contradictionInResult)
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        if (contradictionInResult)
        {
            var result = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!; result["plan_source"] = "rules";
            setup.Pipeline.StreamEvents = [Progress(), new("result", JsonSerializer.SerializeToElement(result))];
        }
        else setup.Pipeline.StreamEvents = [Progress(), Progress("case"), new("result", setup.Pipeline.Result)];
        var events = await ReadStreamAsync(setup);
        Assert.Equal(new[] { "progress", "error" }, events.Select(e => e.Event));
        Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task Unknown_result_modes_are_rejected_instead_of_returning_a_mode_less_answer()
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        var result = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!; result["plan_source"] = "unknown";
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(result);
        var response = Assert.IsType<ObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Synthetic question?")));
        Assert.Equal(502, response.StatusCode); Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task Chat_cannot_hide_clinical_items_behind_an_alternative_raw_answer()
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        var result = JsonNode.Parse(setup.Pipeline.Result.GetRawText())!;
        result["plan_source"] = "chat"; result["chat"] = "RAW_ALTERNATIVE_ANSWER";
        setup.Pipeline.Result = JsonSerializer.SerializeToElement(result);
        var response = Assert.IsType<ObjectResult>(await setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Synthetic question?")));
        Assert.Equal(502, response.StatusCode); Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_problem_coverage_stays_unknown_in_live_and_saved_results(bool caseMode)
    {
        var result = JsonNode.Parse(new FakePipelineClient().Result.GetRawText())!;
        result["plan_source"] = caseMode ? "rules" : "single_question";
        var failed = result["problems"]![0]!.DeepClone();
        failed["title"] = "Failed problem"; failed["status"] = "error"; failed["items"] = new JsonArray();
        var uncovered = failed.DeepClone(); uncovered["title"] = "Absent evidence"; uncovered["status"] = "not_found";
        result["problems"]!.AsArray().Add(failed); result["problems"]!.AsArray().Add(uncovered);
        result["not_covered"] = new JsonArray("Failed problem", "Absent evidence");
        var live = PipelineResultPolicy.ForResponse(JsonSerializer.SerializeToElement(result));
        Assert.Equal("Absent evidence", Assert.Single(live.GetProperty("not_covered").EnumerateArray()).GetString());
        Assert.Equal(3, live.GetProperty("problems").GetArrayLength());
        Assert.Equal("error", live.GetProperty("problems")[1].GetProperty("status").GetString());
        var stored = PipelineResultPolicy.ForStorage(live, caseMode);
        Assert.Equal(caseMode ? "Guideline question 3" : "Absent evidence", Assert.Single(stored.GetProperty("not_covered").EnumerateArray()).GetString());
        if (caseMode) Assert.Contains("coverage is unknown", stored.GetProperty("warnings").GetRawText());
        Assert.Equal(stored.GetRawText(), PipelineResultPolicy.ForStorage(stored, caseMode).GetRawText());
    }

    [Theory]
    [InlineData("result")]
    [InlineData("error")]
    public async Task A_terminal_stream_event_stops_enumeration_and_cannot_be_replaced_by_late_results(string terminal)
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        setup.Pipeline.StreamEvents = [new(terminal, terminal == "result" ? setup.Pipeline.Result : JsonSerializer.SerializeToElement(new { message = "PRIVATE_UPSTREAM_ERROR" })),
            Progress(), new("result", setup.Pipeline.Result), new("invalid", JsonSerializer.SerializeToElement(new { answer = "UNVERIFIED_LATE_ANSWER" }))];
        var events = await ReadStreamAsync(setup);
        Assert.Equal(terminal, Assert.Single(events).Event); Assert.Equal(1, setup.Pipeline.StreamEventsRead);
        Assert.DoesNotContain("PRIVATE_", events[0].Data.GetRawText()); Assert.DoesNotContain("UNVERIFIED_", events[0].Data.GetRawText());
        Assert.Equal(terminal == "result", (await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson != null);
    }

    [Fact]
    public async Task Stream_cancellation_cannot_emit_or_save_a_late_result_even_if_the_client_ignores_the_token()
    {
        var setup = await CreateControllerAsync(); await using var db = setup.Db;
        using var cancellation = new CancellationTokenSource(); setup.Controller.HttpContext.RequestAborted = cancellation.Token;
        setup.Pipeline.StreamEvents = [Progress(), new("result", setup.Pipeline.Result)];
        setup.Pipeline.BeforeStreamEvent = e => { if (e.Event == "result") cancellation.Cancel(); };
        var events = await ReadStreamAsync(setup);
        Assert.Equal("progress", Assert.Single(events).Event); Assert.Equal(cancellation.Token, setup.Pipeline.LastCancellationToken);
        Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task Cancelled_non_stream_analysis_does_not_wait_for_or_save_a_late_result(string privacySetting)
    {
        var setup = await CreateControllerAsync(privacySetting); await using var db = setup.Db;
        using var cancellation = new CancellationTokenSource(); setup.Controller.HttpContext.RequestAborted = cancellation.Token;
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Pipeline.AnalyzeHandler = _ => { reached.TrySetResult(true); return release.Task; };
        var work = setup.Controller.Analyze(setup.Workspace.Id, new AnalyzeDto("Synthetic question?"));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var response = Assert.IsType<StatusCodeResult>(await work.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(499, response.StatusCode);
        release.SetResult(setup.Pipeline.Result); Assert.Equal(cancellation.Token, setup.Pipeline.LastCancellationToken);
        Assert.Null((await db.AnalysisRuns.AsNoTracking().SingleAsync()).ResultJson);
    }
}
