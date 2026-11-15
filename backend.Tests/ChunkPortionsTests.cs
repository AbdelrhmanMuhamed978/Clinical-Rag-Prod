using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace ClinicalRag.Tests;

/// <summary>
/// Fake IHttpClientFactory that creates a fresh HttpClient (with our mock Colab handler)
/// on every CreateClient() call, avoiding "already started one or more requests" errors.
/// </summary>
public class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string? name = null) => new HttpClient(_handler, disposeHandler: false);
}

/// <summary>
/// Mock HttpMessageHandler that emulates Colab chunk job submission and polling.
/// </summary>
public class MockColabHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, JsonElement> _jobResults = new();

    /// <summary>
    /// Set to a function that produces a canned response for a given request.
    /// Return null to simulate a Colab failure (non-success status code).
    /// </summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? RespondAsync { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Get && path.Contains("/chunk/jobs/"))
        {
            var id = path.Split('/').Last();
            if (!_jobResults.TryGetValue(id, out var result))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id, status = "completed", result })
            };
        }

        if (RespondAsync != null)
        {
            var result = await RespondAsync(request);
            if (result == null)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (request.Method == HttpMethod.Post && path.EndsWith("/chunk/jobs") && result.IsSuccessStatusCode)
            {
                var body = await result.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                var id = Guid.NewGuid().ToString("N");
                _jobResults[id] = body.Clone();
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new { id })
                };
            }
            return result;
        }
        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// Build a successful /chunk/run response for the given pages, with one verbatim chunk per page.
    /// </summary>
    public static HttpResponseMessage SuccessResponse(params int[] pages)
    {
        var chunks = pages.Select(p => new
        {
            page = p,
            text = $"Chunk text for page {p}",
            type = "clinical",
            verbatim = true,
            metadata = new
            {
                section_path = $"Section {p}",
                confidence = 0.9,
                strength = "strong"
            }
        }).ToList();

        var body = new
        {
            stats = new { candidates = chunks.Count, accepted = chunks.Count, failed = 0 },
            chunks,
            failed_chunks = new object[0],
            skipped_pages = new object[0],
            pages = pages.Select(p => new { page = p, chars = 100, front_matter = false }),
            profile = new { kind = "guideline", structure = "headings" }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body)
        };
    }

    /// <summary>
    /// Build a /chunk/run response where some pages are skipped (front matter).
    /// </summary>
    public static HttpResponseMessage SkipResponse(int[] chunkedPages, int[] skippedPages)
    {
        var chunks = chunkedPages.Select(p => new
        {
            page = p,
            text = $"Chunk text for page {p}",
            type = "clinical",
            verbatim = true,
            metadata = new
            {
                section_path = $"Section {p}",
                confidence = 0.85,
                strength = "conditional"
            }
        }).ToList();

        var body = new
        {
            stats = new { candidates = chunks.Count, accepted = chunks.Count, failed = 0 },
            chunks,
            failed_chunks = new object[0],
            skipped_pages = skippedPages.Select(p => new { page = p, reason = "front_matter" }),
            pages = chunkedPages.Select(p => new { page = p, chars = 100, front_matter = false })
                .Concat(skippedPages.Select(p => new { page = p, chars = 50, front_matter = true })),
            profile = new { kind = "guideline", structure = "headings" }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body)
        };
    }

    /// <summary>
    /// Build a /chunk/run response with a verbatim-failed chunk for the given page.
    /// </summary>
    public static HttpResponseMessage VerbatimFailResponse(int page)
    {
        var body = new
        {
            stats = new { candidates = 1, accepted = 0, failed = 1 },
            chunks = new object[0],
            failed_chunks = new[]
            {
                new
                {
                    page,
                    text = $"Non-verbatim chunk for page {page}",
                    type = "clinical",
                    verbatim = false,
                    validation_error = "chunk text is not verbatim to source page",
                    metadata = new { section_path = $"Section {page}", confidence = 0.6 }
                }
            },
            skipped_pages = new object[0],
            pages = new[] { new { page, chars = 100, front_matter = false } },
            profile = new { kind = "guideline", structure = "headings" }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body)
        };
    }
}

[Collection("Sequential")]
public class ChunkPortionsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly MockColabHandler _mockHandler = new();

    public ChunkPortionsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient()
    {
        // Override the default HttpClientFactory so CreateClient() uses our mock Colab handler.
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(
                    new FakeHttpClientFactory(_mockHandler));
            });
        }).CreateClient();
    }

    private static ClaimsPrincipal Principal(string firebaseUid, string email) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, firebaseUid),
            new Claim(ClaimTypes.Email, email),
            new Claim("email_verified", "true"),
        }, "Test"));

    private async Task<(Guid workspaceId, Guid docId)> SeedDocumentWithPages(
        CustomWebApplicationFactory factory,
        int pageCount,
        string firebaseUid = "uid-chunk-a")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = db.Users.FirstOrDefault(u => u.FirebaseUid == firebaseUid)
            ?? db.Users.Add(new User { FirebaseUid = firebaseUid, Email = "chunk-a@example.com" }).Entity;
        db.SaveChanges();

        var wsId = Guid.NewGuid();
        var ws = db.Workspaces.Add(new Workspace
        {
            Id = wsId,
            Name = "Chunk WS",
            OwnerId = user.Id
        }).Entity;
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = wsId, UserId = user.Id, Role = "owner"
        });

        // Build parsed text with --- Page N --- markers.
        var sb = new StringBuilder();
        for (int p = 1; p <= pageCount; p++)
        {
            sb.AppendLine($"--- Page {p} ---");
            sb.AppendLine($"Content of page {p}. Some clinical text here.");
        }

        var docId = Guid.NewGuid();
        var doc = db.Documents.Add(new Document
        {
            Id = docId,
            WorkspaceId = wsId,
            FileName = "chunk_test.pdf",
            ParsedText = sb.ToString(),
            PageCount = pageCount,
            Status = "parsed",
            ChunkingStatus = "not_started",
            ChunkedPagesCount = 0,
            ChunkedPageNumbers = "[]"
        }).Entity;
        db.SaveChanges();

        return (wsId, doc.Id);
    }

    private static async Task<HttpResponseMessage> PostChunkPortions(
        HttpClient client, Guid workspaceId, Guid docId, int[] pages,
        string? guidance = null, int maxTokens = 400, int batchSize = 2)
    {
        var body = new Dictionary<string, object> { ["pages"] = pages };
        if (guidance != null) body["guidance"] = guidance;
        if (maxTokens > 0) body["maxTokens"] = maxTokens;
        if (batchSize > 0) body["batchSize"] = batchSize;
        var res = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/documents/{docId}/chunk-portions", body);
        if (!res.IsSuccessStatusCode)
        {
            var errorBody = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"ChunkPortions returned {res.StatusCode}: {errorBody}");
        }
        return res;
    }

    // 7 non-contiguous pages: all must be chunked or explicitly reported (skipped/failed).
    [Fact]
    public async Task Seven_non_contiguous_pages_are_all_chunked_or_reported()
    {
        _mockHandler.RespondAsync = async req =>
        {
            var bodyText = await req.Content.ReadAsStringAsync();
            var pageMatches = System.Text.RegularExpressions.Regex.Matches(bodyText, @"--- Page (\d+) ---");
            var batchPages = pageMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(p => p)
                .ToArray();
            return MockColabHandler.SuccessResponse(batchPages);
        };

        var factory = _factory;
        var (wsId, docId) = await SeedDocumentWithPages(factory, 15);
        var client = CreateClient();
        TestPrincipal.Current = Principal("uid-chunk-a", "chunk-a@example.com");

        // 7 non-contiguous pages.
        var pages = new[] { 1, 3, 5, 7, 9, 12, 15 };
        var res = await PostChunkPortions(client, wsId, docId, pages);

        TestPrincipal.Current = null;

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, json.GetProperty("pagesChunked").GetInt32());
        Assert.Equal(0, json.GetProperty("failedPages").GetArrayLength());

        // Verify all 7 pages are recorded as chunked in the database.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        var chunked = JsonSerializer.Deserialize<List<int>>(doc.ChunkedPageNumbers!) ?? new List<int>();
        foreach (var p in pages)
            Assert.Contains(p, chunked);

        // And chunks exist for all 7 pages.
        var chunkCount = await db.Chunks.CountAsync(c => c.DocumentId == docId);
        Assert.Equal(7, chunkCount);
    }

    // A failing batch leaves its pages un-chunked; earlier batches are saved.
    [Fact]
    public async Task Failing_batch_leaves_its_pages_un_chunked()
    {
        var callIndex = 0;
        _mockHandler.RespondAsync = async req =>
        {
            callIndex++;
            var bodyText = await req.Content.ReadAsStringAsync();
            var pageMatches = System.Text.RegularExpressions.Regex.Matches(bodyText, @"--- Page (\d+) ---");
            var batchPages = pageMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(p => p)
                .ToArray();

            // Batch 2 (pages 3,4) fails with a client error (Colab rejects this batch,
            // but other batches still succeed).
            if (batchPages.SequenceEqual(new[] { 3, 4 }))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            return MockColabHandler.SuccessResponse(batchPages);
        };

        var factory = _factory;
        var (wsId, docId) = await SeedDocumentWithPages(factory, 10);
        var client = CreateClient();
        TestPrincipal.Current = Principal("uid-chunk-a", "chunk-a@example.com");

        // Pages 1-5 with batch size 2 -> batches: [1,2], [3,4], [5]. Batch [3,4] fails.
        var res = await PostChunkPortions(client, wsId, docId, new[] { 1, 2, 3, 4, 5 }, batchSize: 2);

        TestPrincipal.Current = null;

        // Should return 200 with partial progress and failed pages listed.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var failedArr = json.GetProperty("failedPages").EnumerateArray().ToList();
        Assert.True(failedArr.Count > 0);
        var failed = failedArr.Select(e => e.GetInt32()).ToList();
        Assert.Equal(new[] { 3, 4 }, failed.OrderBy(x => x));

        // Pages 1,2,5 are chunked; 3,4 are not.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        var chunked = JsonSerializer.Deserialize<List<int>>(doc.ChunkedPageNumbers!) ?? new List<int>();
        Assert.Contains(1, chunked);
        Assert.Contains(2, chunked);
        Assert.Contains(5, chunked);
        Assert.DoesNotContain(3, chunked);
        Assert.DoesNotContain(4, chunked);

        // Chunks exist only for pages 1,2,5.
        var chunkPages = await db.Chunks.Where(c => c.DocumentId == docId)
            .Select(c => c.Page).Distinct().OrderBy(p => p).ToListAsync();
        Assert.Equal(new[] { 1, 2, 5 }, chunkPages);
    }

    // Skipped pages (front matter) are marked chunked but have no chunks.
    [Fact]
    public async Task Skipped_pages_are_marked_chunked_without_chunks()
    {
        var batchPagesSeen = new List<int[]>();
        _mockHandler.RespondAsync = async req =>
        {
            var bodyText = await req.Content.ReadAsStringAsync();
            var pageMatches = System.Text.RegularExpressions.Regex.Matches(bodyText, @"--- Page (\d+) ---");
            var batchPages = pageMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(p => p)
                .ToArray();
            batchPagesSeen.Add(batchPages);

            // Pages 1 and 3 are skipped (front matter), page 2 gets a chunk.
            var chunked = batchPages.Where(p => p != 1 && p != 3).ToArray();
            var skipped = batchPages.Where(p => p == 1 || p == 3).ToArray();
            return MockColabHandler.SkipResponse(chunked, skipped);
        };

        var factory = _factory;
        var (wsId, docId) = await SeedDocumentWithPages(factory, 5);
        var client = CreateClient();
        TestPrincipal.Current = Principal("uid-chunk-a", "chunk-a@example.com");

        var res = await PostChunkPortions(client, wsId, docId, new[] { 1, 2, 3 }, batchSize: 2);

        TestPrincipal.Current = null;

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("pagesSkipped").GetInt32()); // pages 1 and 3 skipped

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        var chunked = JsonSerializer.Deserialize<List<int>>(doc.ChunkedPageNumbers!) ?? new List<int>();
        // All 3 pages are marked chunked (1 and 3 as skipped, 2 as chunked).
        Assert.Equal(new[] { 1, 2, 3 }, chunked.OrderBy(p => p).ToList());

        // Only page 2 has chunks.
        var chunkPages = await db.Chunks.Where(c => c.DocumentId == docId)
            .Select(c => c.Page).Distinct().OrderBy(p => p).ToListAsync();
        Assert.Equal(new[] { 2 }, chunkPages);
    }

    // Verbatim-failed chunks are stored as pending with ValidationError set.
    [Fact]
    public async Task Verbatim_failed_chunks_are_stored_pending_with_validation_error()
    {
        _mockHandler.RespondAsync = async req =>
        {
            var bodyText = await req.Content.ReadAsStringAsync();
            var pageMatches = System.Text.RegularExpressions.Regex.Matches(bodyText, @"--- Page (\d+) ---");
            var batchPages = pageMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(p => p)
                .ToArray();
            return MockColabHandler.VerbatimFailResponse(batchPages[0]);
        };

        var factory = _factory;
        var (wsId, docId) = await SeedDocumentWithPages(factory, 5);
        var client = CreateClient();
        TestPrincipal.Current = Principal("uid-chunk-a", "chunk-a@example.com");

        var res = await PostChunkPortions(client, wsId, docId, new[] { 2 }, batchSize: 2);

        TestPrincipal.Current = null;

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunk = await db.Chunks.FirstAsync(c => c.DocumentId == docId && c.Page == 2);
        Assert.Equal("pending", chunk.Status);
        Assert.False(chunk.Verbatim);
        Assert.NotNull(chunk.ValidationError);
        Assert.Contains("verbatim", chunk.ValidationError!, StringComparison.OrdinalIgnoreCase);
    }

    // DTO binding test: camelCase JSON body (pages, maxTokens, batchSize) binds correctly.
    [Fact]
    public async Task Dto_binds_from_camel_case_json()
    {
        _mockHandler.RespondAsync = async req =>
        {
            var bodyText = await req.Content.ReadAsStringAsync();
            var pageMatches = System.Text.RegularExpressions.Regex.Matches(bodyText, @"--- Page (\d+) ---");
            var batchPages = pageMatches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(p => p)
                .ToArray();
            return MockColabHandler.SuccessResponse(batchPages);
        };

        var factory = _factory;
        var (wsId, docId) = await SeedDocumentWithPages(factory, 5);
        var client = CreateClient();
        TestPrincipal.Current = Principal("uid-chunk-a", "chunk-a@example.com");

        // Send camelCase JSON (not snake_case max_tokens / batch_size).
        var res = await PostChunkPortions(client, wsId, docId, new[] { 1, 2 }, "test", 400, 2);

        TestPrincipal.Current = null;

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ExtractYear tests via reflection on the private static method.
    [Fact]
    public static void ExtractYear_rejects_numbers_followed_by_units()
    {
        var result1 = InvokeExtractYear("Dose: 2000 mL daily", 5);
        var result2 = InvokeExtractYear("Give 1500 IU of vitamin", 5);
        Assert.Null(result1);
        Assert.Null(result2);
    }

    [Fact]
    public static void ExtractYear_accepts_parenthesised_year()
    {
        var result = InvokeExtractYear("Guideline (2023) update", 5);
        Assert.Equal(2023, result);
    }

    [Fact]
    public static void ExtractYear_accepts_copyright_cue()
    {
        var result = InvokeExtractYear("© 2023 Clinical Society", 5);
        Assert.Equal(2023, result);
    }

    [Fact]
    public static void ExtractYear_accepts_any_year_on_pages_1_2()
    {
        // Page 2: "2000 mL" would be rejected on a later page, but on page 2
        // any non-unit year is accepted... wait, "2000 mL" IS followed by a unit,
        // so it should still be rejected. Use a plain year on page 2.
        var result = InvokeExtractYear("Published in 2019", 2);
        Assert.Equal(2019, result);
    }

    [Fact]
    public static void ExtractYear_rejects_late_page_year_without_cue()
    {
        // Page 10, no cue, not parenthesised: "some text 2005 more text" -> null
        var result = InvokeExtractYear("some text 2005 more text", 10);
        Assert.Null(result);
    }

    private static int? InvokeExtractYear(string text, int pageNumber)
    {
        var method = typeof(DocumentsController).GetMethod(
            "ExtractYear",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(int) },
            null)!;
        return (int?)method.Invoke(null, new object?[] { text, pageNumber })!;
    }
}
