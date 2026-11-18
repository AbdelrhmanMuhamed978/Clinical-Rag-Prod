using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinicalRag.Tests;

public class ChunkJobTests
{
    private sealed class SyntheticChunkTransport : IColabClient
    {
        public bool Timeout { get; init; }
        public int FailPage { get; init; }
        public bool ArtifactText { get; init; }
        public int Calls { get; private set; }

        public Task<JsonElement?> RunChunkJobAsync(JsonElement body, int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Timeout) throw new DependencyUnavailableException("colab", retryable: true, failureCode: "timeout");
            var text = body.GetProperty("text").GetString() ?? "";
            var pages = Regex.Matches(text, @"--- Page (\d+) ---").Select(match => int.Parse(match.Groups[1].Value))
                .Distinct().ToArray();
            if (FailPage > 0 && pages.Contains(FailPage))
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    chunks = Array.Empty<object>(),
                    skipped_pages = Array.Empty<object>()
                }));
            var chunks = pages.Select(page => new
            {
                page,
                text = ArtifactText
                    ? "Screening for pre&amp;#45;emptive therapy antigen–\n positive. (Strong recommendation, moderate&amp;#45;certainty evidence)"
                    : $"Exact synthetic source for page {page}.",
                verbatim = true,
                type = "clinical",
                metadata = ArtifactText
                    ? new { section_path = "TABLES|Treatment|Induction therapy", strength = "none" }
                    : new { section_path = "Synthetic section", strength = "weak" }
            }).ToArray();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                chunks,
                skipped_pages = Array.Empty<object>()
            }));
        }

        public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> HealthAsync() => Task.FromResult(true);
        public Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ColabReadiness(true));
        public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ChunkJobsController Controller(DocumentTestStore store, ClinicalRag.Api.Data.AppDbContext db) =>
        new(db, new FakeCurrentUser(store.User));

    [Fact]
    public async Task Start_returns_202_and_one_active_job_without_storing_page_text()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        var controller = Controller(store, db);

        var response = Assert.IsType<AcceptedResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1, 2], "synthetic guidance", BatchSize: 1, BatchTimeoutSeconds: 60, MaxRetries: 0)));
        var job = await db.ChunkJobs.SingleAsync();
        Assert.Equal("queued", job.Status);
        Assert.Equal("[1,2]", job.PagesJson);
        Assert.DoesNotContain("Exact synthetic source", JsonSerializer.Serialize(ChunkJobSupport.View(job)));
        Assert.Equal(document.Id, job.DocumentId);

        var second = await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null));
        Assert.IsType<ConflictObjectResult>(second);
        Assert.Equal(1, await db.ChunkJobs.CountAsync());
    }

    [Fact]
    public async Task Pause_resume_and_cancel_are_explicit_job_controls()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id, new StartChunkJobDto([1], null));
        var job = await db.ChunkJobs.SingleAsync();

        var paused = Assert.IsType<OkObjectResult>(await controller.Pause(document.WorkspaceId, job.Id));
        Assert.Equal("paused", DocumentTestStore.Json(paused.Value).GetProperty("status").GetString());
        Assert.IsType<AcceptedResult>(await controller.Resume(document.WorkspaceId, job.Id));
        Assert.Equal("queued", (await db.ChunkJobs.SingleAsync()).Status);
        Assert.IsType<OkObjectResult>(await controller.Cancel(document.WorkspaceId, job.Id));
        job = await db.ChunkJobs.SingleAsync();
        Assert.Equal("cancelled", job.Status);
        Assert.Null((await db.Documents.SingleAsync()).ProcessingOperation);
    }

    [Fact]
    public async Task Processor_commits_batches_and_completes_the_job()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 3);
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1, 2, 3], null, BatchSize: 2, BatchTimeoutSeconds: 60, MaxRetries: 0));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        var transport = new SyntheticChunkTransport();
        await new ChunkJobProcessor(db, transport, NullLogger<ChunkJobProcessor>.Instance)
            .ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        var saved = await db.Documents.SingleAsync();
        Assert.Equal("completed", job.Status);
        Assert.Equal("[1,2,3]", job.CompletedPagesJson);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(3, await db.Chunks.CountAsync());
        Assert.Equal("[1,2,3]", saved.ChunkedPageNumbers);
        Assert.Equal("completed", saved.ChunkingStatus);
        Assert.Null(saved.ProcessingOperation);
    }

    [Fact]
    public async Task Completion_preserves_pages_that_were_processed_before_the_job()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 3);
        await using var db = store.CreateContext();
        var saved = await db.Documents.SingleAsync(item => item.Id == document.Id);
        saved.ChunkedPageNumbers = "[1]";
        saved.ChunkedPagesCount = 1;
        db.Chunks.Add(new Chunk
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Page = 1,
            Text = "Exact synthetic source for page 1.", Status = "pending", Verbatim = true
        });
        await db.SaveChangesAsync();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([2, 3], null, BatchSize: 2, BatchTimeoutSeconds: 60, MaxRetries: 0));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        await new ChunkJobProcessor(db, new SyntheticChunkTransport(),
            NullLogger<ChunkJobProcessor>.Instance).ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        saved = await db.Documents.SingleAsync();
        Assert.Equal("[1,2,3]", saved.ChunkedPageNumbers);
        Assert.Equal(3, saved.ChunkedPagesCount);
        Assert.Equal("completed", saved.ChunkingStatus);
        Assert.Equal(3, await db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Timeout_is_recorded_as_a_batch_failure_not_a_false_success()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 1);
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null, BatchTimeoutSeconds: 60, MaxRetries: 0));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        await new ChunkJobProcessor(db, new SyntheticChunkTransport { Timeout = true },
            NullLogger<ChunkJobProcessor>.Instance).ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        Assert.Equal("failed", job.Status);
        Assert.Equal("batch_timeout", job.ErrorCode);
        Assert.Equal("[1]", job.FailedPagesJson);
        Assert.Empty(await db.Chunks.ToListAsync());
    }

    [Fact]
    public async Task Retry_creates_a_new_job_and_keeps_history()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        var old = new ChunkJob
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Status = "failed",
            PagesJson = "[1,2]", FailedPagesJson = "[2]", CompletedPagesJson = "[1]",
            Guidance = ChunkJobSupport.DefaultGuidance, TotalBatches = 1
        };
        db.ChunkJobs.Add(old);
        await db.SaveChangesAsync();
        var controller = Controller(store, db);

        var response = Assert.IsType<AcceptedResult>(await controller.Retry(document.WorkspaceId, old.Id,
            new RetryChunkJobDto("failed")));
        var retry = await db.ChunkJobs.SingleAsync(job => job.Id != old.Id);
        Assert.Equal(old.Id, retry.RetryOfJobId);
        Assert.Equal("[2]", retry.PagesJson);
        Assert.Equal("queued", retry.Status);
        Assert.Equal(2, await db.ChunkJobs.CountAsync());
    }

    [Fact]
    public async Task Processor_normalizes_entities_and_repairs_source_metadata()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 1);
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null, BatchSize: 1, BatchTimeoutSeconds: 60, MaxRetries: 0));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        await new ChunkJobProcessor(db, new SyntheticChunkTransport { ArtifactText = true },
            NullLogger<ChunkJobProcessor>.Instance).ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        var chunk = await db.Chunks.SingleAsync();
        Assert.Contains("pre-emptive", chunk.Text);
        Assert.Contains("antigen–positive", chunk.Text);
        Assert.DoesNotContain("&amp;", chunk.Text);
        Assert.Equal("strong", chunk.Strength);
        Assert.Equal("moderate", chunk.Certainty);
        Assert.Equal("Treatment > Induction therapy", chunk.Section);
        Assert.Equal(0.90, chunk.Confidence);
    }

    [Fact]
    public async Task Processing_state_returns_lightweight_page_counts_and_latest_job_without_text()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 3);
        await store.AddChunkAsync(document, "pending", page: 1);
        await store.AddChunkAsync(document, "accepted", page: 2);
        await using var db = store.CreateContext();
        var saved = await db.Documents.SingleAsync(item => item.Id == document.Id);
        saved.Status = "chunking";
        saved.ProcessingOperation = "chunking";
        saved.ChunkingStatus = "in_progress";
        saved.ChunkedPageNumbers = "[1,2]";
        saved.ChunkedPagesCount = 2;
        db.ChunkJobs.Add(new ChunkJob
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Status = "running",
            PagesJson = "[1,2]", CompletedPagesJson = "[1]", Guidance = ChunkJobSupport.DefaultGuidance
        });
        await db.SaveChangesAsync();

        var controller = store.Controller(db);
        var result = Assert.IsType<OkObjectResult>(await controller.ProcessingState(document.WorkspaceId, document.Id));
        var json = DocumentTestStore.Json(result.Value);
        Assert.Equal("chunking", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("chunkedPagesCount").GetInt32());
        Assert.Equal("[1,2]", json.GetProperty("chunkedPageNumbers").GetRawText());
        Assert.Equal(2, json.GetProperty("chunkCounts").GetProperty("total").GetInt32());
        Assert.Equal(1, json.GetProperty("chunkCounts").GetProperty("pending").GetInt32());
        Assert.Equal(1, json.GetProperty("chunkCounts").GetProperty("accepted").GetInt32());
        Assert.Equal("running", json.GetProperty("job").GetProperty("status").GetString());
        Assert.DoesNotContain("Retained synthetic evidence", json.GetRawText());
    }

    [Fact]
    public async Task Document_delete_removes_terminal_jobs_before_the_restrict_foreign_key()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        db.ChunkJobs.Add(new ChunkJob
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Status = "cancelled",
            PagesJson = "[1]", Guidance = ChunkJobSupport.DefaultGuidance
        });
        await db.SaveChangesAsync();

        var result = await store.Controller(db).Delete(document.WorkspaceId, document.Id);
        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(await db.ChunkJobs.ToListAsync());
        Assert.Empty(await db.Documents.ToListAsync());
    }

    [Fact]
    public async Task Workspace_delete_removes_terminal_jobs_before_the_restrict_foreign_key()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        db.ChunkJobs.Add(new ChunkJob
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Status = "cancelled",
            PagesJson = "[1]", Guidance = ChunkJobSupport.DefaultGuidance
        });
        await db.SaveChangesAsync();

        var controller = new WorkspacesController(db, new FakeCurrentUser(store.User), store.Configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await controller.Delete(document.WorkspaceId);
        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(await db.ChunkJobs.ToListAsync());
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.NotNull((await db.Workspaces.SingleAsync()).DeletedAt);
    }

    [Fact]
    public async Task Retry_remaining_excludes_completed_pages_and_preserves_the_original_job()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 3);
        await using var db = store.CreateContext();
        var old = new ChunkJob
        {
            WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Status = "partial",
            PagesJson = "[1,2,3]", CompletedPagesJson = "[1]", Guidance = ChunkJobSupport.DefaultGuidance
        };
        db.ChunkJobs.Add(old);
        await db.SaveChangesAsync();
        var controller = Controller(store, db);

        Assert.IsType<AcceptedResult>(await controller.Retry(document.WorkspaceId, old.Id,
            new RetryChunkJobDto("remaining")));
        var retry = await db.ChunkJobs.SingleAsync(job => job.Id != old.Id);
        Assert.Equal("[2,3]", retry.PagesJson);
        Assert.Equal(old.Id, retry.RetryOfJobId);
        db.ChangeTracker.Clear();
        Assert.Equal("[1,2,3]", (await db.ChunkJobs.SingleAsync(job => job.Id == old.Id)).PagesJson);
    }

    [Fact]
    public async Task Start_rejects_invalid_settings_pages_and_over_long_guidance()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        var controller = Controller(store, db);

        Assert.IsType<BadRequestObjectResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null, BatchSize: 0)));
        Assert.IsType<BadRequestObjectResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null, BatchTimeoutSeconds: 30)));
        Assert.IsType<BadRequestObjectResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], null, FailureMode: "ignore")));
        Assert.IsType<BadRequestObjectResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([], null)));
        Assert.IsType<BadRequestObjectResult>(await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], new string('x', 4001))));
        Assert.Empty(await db.ChunkJobs.ToListAsync());
    }

    [Fact]
    public async Task Active_returns_the_reattach_view_without_persisted_guidance_text()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync();
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1], "PRIVATE_SYNTHETIC_GUIDANCE", BatchSize: 1));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "paused";
        await db.SaveChangesAsync();

        var active = Assert.IsType<OkObjectResult>(await controller.Active(document.WorkspaceId, document.Id));
        var json = DocumentTestStore.Json(active.Value);
        Assert.Equal("paused", json.GetProperty("job").GetProperty("status").GetString());
        Assert.DoesNotContain("PRIVATE_SYNTHETIC_GUIDANCE", json.GetRawText());
    }

    [Theory]
    [InlineData("stop", "failed", 0, 1)]
    [InlineData("continue", "partial", 1, 2)]
    public async Task Failure_mode_stops_or_continues_after_an_unaccounted_page(
        string failureMode, string expectedStatus, int expectedChunks, int expectedCalls)
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 2);
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1, 2], null, BatchSize: 1, BatchTimeoutSeconds: 60, MaxRetries: 0,
                FailureMode: failureMode));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        var transport = new SyntheticChunkTransport { FailPage = 1 };
        await new ChunkJobProcessor(db, transport, NullLogger<ChunkJobProcessor>.Instance)
            .ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        Assert.Equal(expectedStatus, job.Status);
        Assert.Equal("[1]", job.FailedPagesJson);
        Assert.Equal("empty_batch_result", job.ErrorCode);
        Assert.Equal(expectedCalls, transport.Calls);
        Assert.Equal(expectedChunks, await db.Chunks.CountAsync());
    }

    [Fact]
    public async Task Pause_is_observed_before_the_next_batch_and_resume_processes_the_remaining_pages()
    {
        using var store = new DocumentTestStore();
        var document = await store.AddDocumentAsync(pageCount: 2);
        await using var db = store.CreateContext();
        var controller = Controller(store, db);
        await controller.Start(document.WorkspaceId, document.Id,
            new StartChunkJobDto([1, 2], null, BatchSize: 1, BatchTimeoutSeconds: 60, MaxRetries: 0));
        var job = await db.ChunkJobs.SingleAsync();
        job.Status = "pausing";
        job.PauseRequested = true;
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();

        var transport = new SyntheticChunkTransport();
        await new ChunkJobProcessor(db, transport, NullLogger<ChunkJobProcessor>.Instance)
            .ProcessAsync(job.Id, "test-owner", CancellationToken.None);
        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        Assert.Equal("paused", job.Status);
        Assert.Equal("[]", job.CompletedPagesJson);
        Assert.Equal(0, transport.Calls);
        Assert.Empty(await db.Chunks.ToListAsync());

        Assert.IsType<AcceptedResult>(await controller.Resume(document.WorkspaceId, job.Id));
        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        job.Status = "running";
        job.LeaseOwner = "test-owner";
        job.LeaseUntil = DateTime.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();
        await new ChunkJobProcessor(db, transport, NullLogger<ChunkJobProcessor>.Instance)
            .ProcessAsync(job.Id, "test-owner", CancellationToken.None);

        db.ChangeTracker.Clear();
        job = await db.ChunkJobs.SingleAsync();
        Assert.Equal("completed", job.Status);
        Assert.Equal("[1,2]", job.CompletedPagesJson);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, await db.Chunks.CountAsync());
    }
}
