using System.Net;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ClinicalRag.Tests;

/// <summary>Relational, isolated state: race assertions depend on real rollback and revision checks.</summary>
internal sealed class DocumentTestStore : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly List<string> _files = [];
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", $"clinical-rag-document-tests-{Guid.NewGuid():N}");
    public string Uploads => Path.Combine(Root, "uploads");
    public DbContextOptions<AppDbContext> Options { get; }
    public User User { get; } = new() { FirebaseUid = "synthetic-documents-user", Email = "synthetic@example.com" };
    public Workspace Workspace { get; }
    public IConfiguration Configuration { get; }
    public ChunkVectorCache VectorCache { get; } = new();

    public DocumentTestStore()
    {
        Directory.CreateDirectory(Uploads);
        _connection.Open();
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        Workspace = new Workspace { Name = "Synthetic documents", OwnerId = User.Id };
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Users.Add(User); db.Workspaces.Add(Workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = Workspace.Id, UserId = User.Id, Role = "owner" });
        db.SaveChanges();
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:UploadsDirectory"] = Uploads,
            ["Colab:RetrieveUrl"] = "http://colab.test",
            ["Colab:ApiKey"] = "synthetic-colab-key",
            ["Colab:TimeoutSeconds"] = "30",
            ["Colab:ChunkJobPollMilliseconds"] = "100",
            ["Embedding:RetryBaseMilliseconds"] = "0"
        }).Build();
    }

    public AppDbContext CreateContext() => new(Options, VectorCache);
    public string CreateFile(bool insideUploads = true)
    {
        var path = Path.Combine(insideUploads ? Uploads : Root, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "%PDF-1.7 Synthetic fixture, never patient data.");
        _files.Add(path); return path;
    }

    public async Task<Document> AddDocumentAsync(string status = "parsed", bool parsed = true, int pageCount = 3)
    {
        var document = new Document
        {
            WorkspaceId = Workspace.Id, FileName = "synthetic.pdf", StoragePath = CreateFile(),
            Status = status, Society = "Synthetic Society", Year = 2024, GuidelineStatus = "draft",
            ParsedText = parsed ? string.Join('\n', Enumerable.Range(1, pageCount).Select(p => $"--- Page {p} ---\nSource words for page {p}.")) : null,
            PageCount = parsed ? pageCount : null, ChunkedPageNumbers = "[]"
        };
        await using var db = CreateContext(); db.Documents.Add(document); await db.SaveChangesAsync(); return document;
    }

    public async Task<Chunk> AddChunkAsync(Document document, string status = "pending", string? vector = null, int page = 1)
    {
        var chunk = new Chunk { DocumentId = document.Id, WorkspaceId = document.WorkspaceId, Page = page,
            Text = $"Retained synthetic evidence {page}.", Status = status, Verbatim = true };
        if (vector != null) EmbeddingTestData.Set(chunk, JsonSerializer.Deserialize<float[]>(vector)!);
        await using var db = CreateContext(); db.Chunks.Add(chunk); await db.SaveChangesAsync(); return chunk;
    }

    public DocumentsController Controller(AppDbContext db, IColabClient? colab = null, IHttpClientFactory? http = null,
        CancellationToken cancellationToken = default)
    {
        http ??= new FakeHttpClientFactory(new DocumentHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var transport = TestColabClients.Create(Configuration, http);
        colab ??= new FakeColabClient();
        if (colab is FakeColabClient fake) fake.Transport = transport;
        return new DocumentsController(db, colab, Configuration, new FakeCurrentUser(User),
            new DocumentParser(db, transport, Configuration))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = cancellationToken } }
        };
    }

    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    public void Dispose()
    {
        _connection.Dispose();
        VectorCache.Dispose();
        // Remove only files/directories created by this unique fixture, not broad temp paths.
        foreach (var path in _files) if (File.Exists(path)) File.Delete(path);
        foreach (var path in Directory.EnumerateFiles(Uploads)) File.Delete(path);
        Directory.Delete(Uploads); Directory.Delete(Root);
    }
}

internal sealed class DocumentHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
