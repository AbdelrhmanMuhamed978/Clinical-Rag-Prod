using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/analyze")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
[RequestSizeLimit(1024 * 1024)]
public class AnalysisController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IColabClient _colab;
    private readonly IConfigService _configService;
    private readonly IPipelineClient _pipeline;
    private readonly ICurrentUser _currentUser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalysisController> _log;
    private readonly RuleService _rules;
    private readonly ChunkSearchService _search;
    private readonly EmbeddingBatchClient _embeddings;

    public AnalysisController(
        AppDbContext db,
        IColabClient colab,
        IConfigService configService,
        IPipelineClient pipeline,
        ICurrentUser currentUser,
        IConfiguration configuration,
        ILogger<AnalysisController> log,
        RuleService? rules = null,
        ChunkSearchService? search = null,
        EmbeddingBatchClient? embeddings = null)
    {
        _db = db;
        _colab = colab;
        _configService = configService;
        _pipeline = pipeline;
        _currentUser = currentUser;
        _configuration = configuration;
        _log = log;
        _rules = rules ?? new RuleService(db);
        _search = search ?? new ChunkSearchService(db, db.VectorCache);
        _embeddings = embeddings ?? new EmbeddingBatchClient(colab, configuration);
    }

    [HttpPost]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Analyze(Guid workspaceId, [FromBody] AnalyzeDto dto)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var prepared = await PrepareAsync(workspaceId, dto, cancellationToken);
        using var timing = OperationTelemetry.Start("analysis.total", _log, dto.Message);
        try
        {
            var result = await _pipeline.AnalyzeAsync(
                prepared.Request,
                prepared.Authorization,
                cancellationToken).WaitAsync(cancellationToken);
            var safeResponse = PipelineResultPolicy.ForResponse(result);
            cancellationToken.ThrowIfCancellationRequested();
            await PersistResultAsync(prepared.Run, safeResponse, prepared.StoreResult, prepared.CaseMode);
            Response.Headers.CacheControl = "no-store";
            timing.Success();
            return Ok(safeResponse);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            timing.Cancelled();
            return StatusCode(499);
        }
        catch (DependencyUnavailableException unavailable) { return Unavailable(unavailable); }
        catch (Exception ex)
        {
            _log.LogWarning("Verified pipeline request failed for workspace {WorkspaceId} ({ExceptionType})",
                workspaceId, ex.GetType().Name);
            return StatusCode(502, new { error = "Verified analysis pipeline failed." });
        }
    }

    [HttpPost("stream")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task Stream(Guid workspaceId, [FromBody] AnalyzeDto dto)
    {
        var prepared = await PrepareAsync(workspaceId, dto, HttpContext.RequestAborted);
        using var timing = OperationTelemetry.Start("analysis.stream", _log, dto.Message);
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            var terminalEvent = false;
            string? authoritativeMode = null;
            await foreach (var pipelineEvent in _pipeline.StreamAnalyzeAsync(
                prepared.Request,
                prepared.Authorization,
                HttpContext.RequestAborted))
            {
                HttpContext.RequestAborted.ThrowIfCancellationRequested();
                if (pipelineEvent.Event is not ("progress" or "result" or "error"))
                    throw new InvalidDataException("Invalid pipeline stream event.");
                var data = pipelineEvent.Event == "result"
                    ? PipelineResultPolicy.ForResponse(pipelineEvent.Data)
                    : StreamMessage(pipelineEvent);
                var eventMode = pipelineEvent.Event == "result" ? PipelineResultPolicy.Mode(data)
                    : data.TryGetProperty("mode", out var mode) ? mode.GetString() : null;
                if (eventMode != null)
                {
                    if (authoritativeMode != null && authoritativeMode != eventMode)
                        throw new InvalidDataException("Contradictory pipeline stream mode.");
                    authoritativeMode = eventMode;
                }
                if (pipelineEvent.Event == "result")
                {
                    await PersistResultAsync(prepared.Run, data, prepared.StoreResult, prepared.CaseMode);
                    timing.Success();
                }

                await Response.WriteAsync($"event: {pipelineEvent.Event}\n", HttpContext.RequestAborted);
                await Response.WriteAsync($"data: {data.GetRawText()}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                if (pipelineEvent.Event is "result" or "error")
                {
                    terminalEvent = true;
                    break;
                }
            }
            if (!terminalEvent) throw new InvalidDataException("Pipeline stream ended without a result.");
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            timing.Cancelled();
            // Client disconnected; the audit row intentionally remains without a result.
        }
        catch (DependencyUnavailableException unavailable)
        {
            if (HttpContext.RequestAborted.IsCancellationRequested) return;
            if (!Response.HasStarted)
            {
                Response.ContentType = "application/json";
                Response.Headers.Remove("X-Accel-Buffering");
                await RequestObservabilityMiddleware.UnavailableAsync(HttpContext, unavailable);
            }
            else
            {
                var error = JsonSerializer.Serialize(new { message = unavailable.PublicMessage, dependency = unavailable.Service,
                    retryAfterSeconds = unavailable.RetryAfterSeconds, requestId = HttpContext.TraceIdentifier });
                await Response.WriteAsync($"event: error\ndata: {error}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Verified pipeline stream failed for workspace {WorkspaceId} ({ExceptionType})",
                workspaceId, ex.GetType().Name);
            if (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                var error = JsonSerializer.Serialize(new { message = "Verified analysis pipeline failed." });
                await Response.WriteAsync($"event: error\ndata: {error}\n\n");
                await Response.Body.FlushAsync();
            }
        }
    }

    private static JsonElement StreamMessage(PipelineStreamEvent pipelineEvent)
    {
        if (pipelineEvent.Event == "error")
        {
            var dependency = pipelineEvent.Data.ValueKind == JsonValueKind.Object && pipelineEvent.Data.TryGetProperty("dependency", out var service)
                && service.ValueKind == JsonValueKind.String ? service.GetString() : null;
            return dependency is "colab" or "pipeline"
                ? JsonSerializer.SerializeToElement(new { message = new DependencyUnavailableException(dependency).PublicMessage, dependency, retryAfterSeconds = 30 })
                : JsonSerializer.SerializeToElement(new { message = "Verified analysis pipeline failed." });
        }
        if (pipelineEvent.Data.ValueKind != JsonValueKind.Object
            || !pipelineEvent.Data.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString())
            || message.GetString()!.Length > 500)
            throw new InvalidDataException("Invalid pipeline progress event.");
        var projected = new Dictionary<string, string?> { ["message"] = message.GetString() };
        var fields = new[] { "stage", "state", "stepId", "mode" };
        if (fields.Any(name => pipelineEvent.Data.TryGetProperty(name, out _)))
        {
            foreach (var name in fields)
            {
                if (!pipelineEvent.Data.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid pipeline progress metadata.");
                projected[name] = field.GetString();
            }
            if (projected["stage"] is not ("routing" or "facts" or "planning" or "retrieval" or "verification" or "patient_checks")
                || projected["state"] is not ("started" or "completed" or "failed")
                || projected["mode"] is not ("case" or "question" or "chat")
                || projected["stepId"] is not { Length: >= 1 and <= 64 }
                || !Regex.IsMatch(projected["stepId"]!, @"\A[a-z_]+(?:-[0-9]+)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                throw new InvalidDataException("Invalid pipeline progress metadata.");
        }
        return JsonSerializer.SerializeToElement(projected);
    }

    [HttpPost("retrieve")]
    public async Task<IActionResult> Retrieve(Guid workspaceId, [FromBody] RetrieveDto dto)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await DoRetrieve(workspaceId, dto.Query, HttpContext.RequestAborted)); }
        catch (EmbeddingModelMismatchException)
        {
            return Conflict(new { error = "Embedding model or dimensions do not match. Re-embed the workspace with a single model." });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499); }
        catch (DependencyUnavailableException unavailable) { return Unavailable(unavailable); }
        catch (Exception exception)
        {
            _log.LogWarning("Retrieval failed for workspace {WorkspaceId} ({ExceptionType})", workspaceId, exception.GetType().Name);
            return StatusCode(502, new { error = "Retrieval failed. Check the embedding service and workspace index." });
        }
    }

    private IActionResult Unavailable(DependencyUnavailableException unavailable)
    {
        Response.Headers.RetryAfter = unavailable.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(503, new { error = unavailable.PublicMessage, dependency = unavailable.Service, requestId = HttpContext.TraceIdentifier });
    }

    private sealed record PreparedAnalysis(
        AnalysisRun Run,
        PipelineAnalyzeRequest Request,
        string? Authorization,
        bool StoreResult,
        bool CaseMode);

    private async Task<PreparedAnalysis> PrepareAsync(
        Guid workspaceId,
        AnalyzeDto dto,
        CancellationToken cancellationToken)
    {
        var user = await _currentUser.EnsureUserAsync();
        var config = await _configService.GetPublishedAsync(workspaceId);
        var configJson = JsonSerializer.Deserialize<JsonElement>(config.ConfigJson);
        var ruleSnapshot = await _rules.ApprovedSnapshotAsync(workspaceId, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dto.Message)))
            .ToLowerInvariant();
        var run = new AnalysisRun
        {
            WorkspaceId = workspaceId,
            UserId = user.Id,
            InputHash = hash,
            ConfigVersion = config.Version
        };
        _db.AnalysisRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var history = (dto.History ?? Array.Empty<ConversationTurnDto>())
            .TakeLast(6)
            .Select(turn => new PipelineConversationTurn(
                turn.Question[..Math.Min(turn.Question.Length, 2000)],
                turn.VerifiedSummary[..Math.Min(turn.VerifiedSummary.Length, 4000)]))
            .ToList();
        var retrieveUrl = BuildRetrieveUrl(workspaceId);
        var request = new PipelineAnalyzeRequest(
            dto.Message,
            configJson,
            ruleSnapshot.Rules,
            history,
            workspaceId.ToString(),
            retrieveUrl,
            dto.ConversationId,
            ruleSnapshot.DrugCatalog,
            ruleSnapshot.CatalogRevision);

        var wordCount = Regex.Matches(dto.Message, @"\S+").Count;
        var caseMode = wordCount >= 40 || history.Any(turn => Regex.Matches(turn.Question, @"\S+").Count >= 40);
        var configuredStoreResults = _configuration["Privacy:StoreResults"];
        var storeResult = configuredStoreResults == null
            ? !caseMode
            : bool.TryParse(configuredStoreResults, out var configured) && configured;
        var authorization = Request.Headers.Authorization.ToString();
        return new PreparedAnalysis(run, request, authorization, storeResult, caseMode);
    }

    private string BuildRetrieveUrl(Guid workspaceId)
    {
        var configuredBase = _configuration["Backend:PublicUrl"]?.TrimEnd('/');
        var baseUrl = !string.IsNullOrWhiteSpace(configuredBase)
            ? configuredBase
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        return $"{baseUrl}/api/workspaces/{workspaceId}/analyze/retrieve";
    }

    private async Task PersistResultAsync(AnalysisRun run, JsonElement result, bool storeResult, bool caseMode)
    {
        if (!storeResult)
            return;
        run.ResultJson = PipelineResultPolicy.ForStorage(result, caseMode).GetRawText();
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
    }

    public async Task<object> DoRetrieve(Guid workspaceId, string query, CancellationToken cancellationToken = default)
    {
        using var timing = OperationTelemetry.Start("retrieval.total", _log, query);
        try { var result = await RetrieveCoreAsync(workspaceId, query, cancellationToken); timing.Success(); return result; }
        catch (OperationCanceledException) { timing.Cancelled(); throw; }
    }

    private async Task<object> RetrieveCoreAsync(Guid workspaceId, string query, CancellationToken cancellationToken)
    {
        var config = await _configService.GetPublishedAsync(workspaceId);
        var retrieval = default(JsonElement);
        try
        {
            var configJson = JsonSerializer.Deserialize<JsonElement>(config.ConfigJson);
            if (configJson.ValueKind == JsonValueKind.Object && configJson.TryGetProperty("retrieval", out var settings)
                && settings.ValueKind == JsonValueKind.Object)
                retrieval = settings;
        }
        catch (JsonException) { /* Legacy rows may predate schema validation. Use safe defaults, never log their content. */ }
        var minRerank = RetrievalNumber(retrieval, "minRerank");
        var topK = RetrievalInteger(retrieval, "topK");
        var candidates = RetrievalInteger(retrieval, "candidates");

        // Query planning/decomposition belongs to the verified Python pipeline.
        // This endpoint embeds and retrieves exactly the focused query it receives.
        var subQueries = new List<string> { query };
        var embedding = await _embeddings.EmbedAsync(subQueries, cancellationToken);
        var ids = await _search.CandidatesAsync(workspaceId, query, embedding, candidates, cancellationToken);
        if (ids.Count == 0)
            return new { context = "NOT_FOUND", chunks = new List<object>(), subQueries };

        // Only shortlisted, current evidence metadata crosses the database on a
        // warm query. No vector/legacy JSON columns or tracked stale review rows.
        // HashSet membership stays O(1) for client-evaluating test providers;
        // relational providers still translate it to a parameterized IN query.
        var shortlist = ids.ToHashSet();
        var current = await _db.Chunks.AsNoTracking()
            .Where(chunk => chunk.WorkspaceId == workspaceId && shortlist.Contains(chunk.Id)
                && (chunk.Status == "accepted" || chunk.Status == "embedded")
                && chunk.Embedding != null && chunk.Embedding.Length == embedding.Dimensions * sizeof(float)
                && chunk.EmbeddingModel == embedding.Model)
            .Select(chunk => new RetrievalChunk(chunk.Id, chunk.EmbeddingModel, chunk.Text, chunk.Section, chunk.SectionHierarchy, chunk.Page, chunk.Strength,
                chunk.Document == null ? null : chunk.Document.GuidelineStatus,
                chunk.Document == null ? null : chunk.Document.Society,
                chunk.Document == null ? null : chunk.Document.FileName,
                chunk.Document == null ? null : chunk.Document.Year))
            .ToListAsync(cancellationToken);
        // SQL equality ignores trailing spaces even with BIN2 collation. Do not
        // accept a changed/invalid label from a warm cache on that weaker check.
        var byId = current.Where(chunk => StringComparer.Ordinal.Equals(chunk.EmbeddingModel, embedding.Model))
            .ToDictionary(chunk => chunk.Id);
        var filtered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (filtered.Count == 0)
            return new { context = "NOT_FOUND", chunks = new List<object>(), subQueries };

        var rerankResponse = await _colab.RerankAsync(query,
            filtered.Select(chunk => RetrievalLimits.RerankPassage(chunk.Text, chunk.Section)).ToList(), cancellationToken).WaitAsync(cancellationToken);
        var rerankScores = RerankScores(rerankResponse, filtered.Count);
        var reranked = filtered.Zip(rerankScores, (chunk, score) => (chunk, score))
            .Where(item => item.score >= minRerank)
            .OrderByDescending(item => item.score)
            .Take(topK)
            .ToList();
        if (reranked.Count == 0)
            return new { context = "NOT_FOUND", chunks = new List<object>(), subQueries };

        var contextParts = new List<string>();
        var results = new List<object>();
        var unknownAsDraft = false;
        for (var index = 0; index < reranked.Count; index++)
        {
            var (chunk, score) = reranked[index];
            var rawStatus = chunk.GuidelineStatus ?? "unknown";
            var status = rawStatus.Equals("final", StringComparison.OrdinalIgnoreCase) ? "final" : "draft";
            unknownAsDraft |= rawStatus.Equals("unknown", StringComparison.OrdinalIgnoreCase);
            var society = chunk.Society ?? chunk.FileName ?? "";
            var year = chunk.Year?.ToString() ?? "";
            var section = chunk.SectionHierarchy ?? chunk.Section ?? "General";
            var strength = chunk.Strength ?? "";
            var strengthSuffix = string.IsNullOrEmpty(strength) ? "" : $" | {strength}";
            contextParts.Add(
                $"[S{index + 1}] {society} {year} ({status.ToUpperInvariant()}) | {section} | p.{chunk.Page}{strengthSuffix}\n{chunk.Text}\n");
            results.Add(new
            {
                chunk_id = chunk.Id.ToString(), society, year, status, section,
                page = chunk.Page, strength, text = chunk.Text,
                rerank_score = Math.Round(score, 4)
            });
        }

        var response = new Dictionary<string, object>
        {
            ["context"] = string.Join("", contextParts),
            ["chunks"] = results,
            ["subQueries"] = subQueries
        };
        if (unknownAsDraft)
            response["unknownGuidelineStatusTreatedAsDraft"] = true;
        return response;
    }

    private static JsonElement RetrievalRule(string name) => ConfigSchema.Schema.GetProperty("properties")
        .GetProperty("retrieval").GetProperty("properties").GetProperty(name);

    private static double RetrievalNumber(JsonElement retrieval, string name)
    {
        var rule = RetrievalRule(name);
        return retrieval.ValueKind == JsonValueKind.Object && retrieval.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && number >= rule.GetProperty("minimum").GetDouble() && number <= rule.GetProperty("maximum").GetDouble()
                ? number : rule.GetProperty("default").GetDouble();
    }

    private static int RetrievalInteger(JsonElement retrieval, string name)
    {
        var rule = RetrievalRule(name);
        // JSON Schema integers include whole-number decimal/exponent forms (10.0, 1e1).
        // TryGetInt32 rejects those lexical forms even though draft validation accepts them.
        return retrieval.ValueKind == JsonValueKind.Object && retrieval.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && Math.Truncate(number) == number
            && number >= rule.GetProperty("minimum").GetInt32() && number <= rule.GetProperty("maximum").GetInt32()
                ? (int)number : rule.GetProperty("default").GetInt32();
    }

    private sealed record RetrievalChunk(Guid Id, string? EmbeddingModel, string Text, string? Section, string? SectionHierarchy,
        int Page, string? Strength, string? GuidelineStatus, string? Society, string? FileName, int? Year);

    private static IReadOnlyList<double> RerankScores(JsonElement response, int expectedCount)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("scores", out var scores)
            || scores.ValueKind != JsonValueKind.Array || scores.GetArrayLength() != expectedCount)
            throw new InvalidDataException("Unexpected rerank score count.");
        var result = new List<double>(expectedCount);
        foreach (var element in scores.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var score)
                || !double.IsFinite(score) || score is < 0 or > 1)
                throw new InvalidDataException("Invalid rerank score.");
            result.Add(score);
        }
        return result;
    }
}

public sealed record ConversationTurnDto(
    [Required, StringLength(2000, MinimumLength = 1)] string Question,
    [Required, StringLength(4000, MinimumLength = 1)] string VerifiedSummary);
public sealed record AnalyzeDto(
    [Required, StringLength(100000, MinimumLength = 1)] string Message,
    [StringLength(128)] string? ConversationId = null,
    [MaxLength(64)] IReadOnlyList<ConversationTurnDto>? History = null);
public sealed record RetrieveDto([Required, StringLength(4000, MinimumLength = 1)] string Query);
