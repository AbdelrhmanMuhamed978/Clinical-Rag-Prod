using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Exercises production DoRetrieve, not a Python reimplementation of search.
// The same corpus, fixed vectors and stub reranker are used before and after.
const int dimensions = 768;
var arguments = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
var fixtureBytes = await File.ReadAllBytesAsync(arguments["--fixture"]);
var fixture = JsonSerializer.Deserialize<Fixture>(fixtureBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
var iterations = int.Parse(arguments["--iterations"], CultureInfo.InvariantCulture);
var count = int.Parse(arguments["--chunks"], CultureInfo.InvariantCulture);
var provider = arguments.GetValueOrDefault("--provider", "inmemory");
var measureSearch = bool.Parse(arguments.GetValueOrDefault("--measure-search", "false"));
var checks = new List<object>();
var recalls = new List<double>();
var reciprocals = new List<double>();
var ndcgs = new List<double>();
var candidateRecalls = new List<double>();

using (var corpus = new Corpus("quality", provider))
{
    var queryCases = new List<(string Name, string Query, float[] Vector, Guid Relevant)>();
    var id = 1;
    for (var topic = 0; topic < fixture.Topics.Count; topic++)
    {
        var item = fixture.Topics[topic];
        var exactVector = Unit(topic * 2);
        // Exact-term stress: topical distractors are closer in the fixed dense
        // fixtures. No real embedding model is being characterised by this setup.
        for (var distractor = 0; distractor < 9; distractor++)
            corpus.Add(id++, $"{item.Key} synthetic general background {distractor}.", "General background", exactVector);
        var relevantVector = Unit(topic * 2);
        relevantVector[dimensions - 1] = 0.25f;
        Normalize(relevantVector);
        var exactId = corpus.Add(id++, item.Text, item.Section, relevantVector);
        var semanticVector = Unit(topic * 2 + 1);
        var semanticId = corpus.Add(id++, item.SemanticText, "Synthetic semantic source", semanticVector);
        queryCases.Add(($"{item.Key}-exact", item.ExactQuery, exactVector, exactId));
        queryCases.Add(($"{item.Key}-semantic", item.SemanticQuery, semanticVector, semanticId));
    }
    await corpus.Db.SaveChangesAsync();
    foreach (var test in queryCases)
    {
        corpus.Colab.Vector = test.Vector;
        var response = JsonSerializer.SerializeToElement(await corpus.Controller.DoRetrieve(corpus.WorkspaceId, test.Query));
        var ids = response.GetProperty("chunks").EnumerateArray().Select(chunk => Guid.Parse(chunk.GetProperty("chunk_id").GetString()!)).ToList();
        var rank = ids.IndexOf(test.Relevant) + 1;
        var hit = rank > 0 ? 1.0 : 0.0;
        var candidateHit = corpus.Colab.LastPassages.Any(passage => passage.Contains(corpus.TextById[test.Relevant], StringComparison.Ordinal)) ? 1.0 : 0.0;
        recalls.Add(hit); reciprocals.Add(rank > 0 ? 1.0 / rank : 0.0);
        ndcgs.Add(rank > 0 ? 1.0 / Math.Log2(rank + 1) : 0.0); candidateRecalls.Add(candidateHit);
        checks.Add(new { test.Name, relevantRank = rank == 0 ? (int?)null : rank, candidateHit = candidateHit == 1 });
    }
}

var latencies = new List<double>();
var searchLatencies = new List<double>();
long storedVectorBytes;
using (var corpus = new Corpus("latency", provider))
{
    var random = new Random(20261115);
    float[]? queryVector = null;
    for (var index = 0; index < count; index++)
    {
        var vector = Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        Normalize(vector);
        if (index == 7) queryVector = vector;
        corpus.Add(index + 1, $"Synthetic performance source {index}. Benchmark topic {index % 32}.", $"Section {index % 32}", vector);
    }
    await corpus.Db.SaveChangesAsync();
    corpus.Colab.Vector = queryVector!;
    for (var warmup = 0; warmup < 3; warmup++) await corpus.Controller.DoRetrieve(corpus.WorkspaceId, "benchmark topic 7");
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var timer = Stopwatch.StartNew();
        await corpus.Controller.DoRetrieve(corpus.WorkspaceId, "benchmark topic 7");
        latencies.Add(timer.Elapsed.TotalMilliseconds);
    }
    storedVectorBytes = corpus.StoredVectorBytes;
    if (measureSearch)
    {
        var search = new ChunkSearchService(corpus.Db, corpus.Db.VectorCache);
        var embedding = EmbeddingVectors.Parse(await corpus.Colab.EmbedAsync(["benchmark topic 7"]), 1);
        for (var warmup = 0; warmup < 3; warmup++) await search.CandidatesAsync(corpus.WorkspaceId, "benchmark topic 7", embedding, 30);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var timer = Stopwatch.StartNew();
            await search.CandidatesAsync(corpus.WorkspaceId, "benchmark topic 7", embedding, 30);
            searchLatencies.Add(timer.Elapsed.TotalMilliseconds);
        }
        searchLatencies.Sort();
    }
}

latencies.Sort();
var result = new
{
    scope = provider == "sqlserver"
        ? "Offline synthetic regression; fixed vectors, constant reranking, disposable SQL Server database with BM25 fallback. Includes SQL execution but no model/GPU/network-service latency or clinical-quality claim. Not publication evidence."
        : "Offline synthetic regression; fixed vectors, constant reranking, EF InMemory. No GPU/network/SQL latency or clinical-quality claim. Not Prompt 11 publication evidence.",
    fixtureSha256 = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant(),
    generatedAtUtc = DateTime.UtcNow,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    databaseProvider = provider,
    vectorStorage = typeof(Chunk).GetProperty(nameof(Chunk.Embedding))!.PropertyType == typeof(byte[]) ? "float32 binary" : "JSON nvarchar",
    quality = new { queries = recalls.Count, recallAt8 = recalls.Average(), mrrAt8 = reciprocals.Average(), ndcgAt8 = ndcgs.Average(), candidateRecallAt30 = candidateRecalls.Average(), cases = checks },
    performance = new
    {
        chunks = count, dimensions, iterations, warmupIterations = 3,
        measuredPath = $"Warm production DoRetrieve with stub embedding/reranking and {provider}",
        p50Milliseconds = Math.Round(Percentile(latencies, .50), 3),
        p95Milliseconds = Math.Round(Percentile(latencies, .95), 3),
        maxMilliseconds = Math.Round(latencies[^1], 3), storedVectorBytes,
        targetUnder100Milliseconds = Percentile(latencies, .95) < 100
    },
    searchPerformance = searchLatencies.Count == 0 ? null : new
    {
        measuredPath = "Warm production CandidatesAsync: cached SIMD dense + BM25 + RRF, no source metadata query or model calls",
        chunks = count, dimensions, iterations, warmupIterations = 3,
        p50Milliseconds = Math.Round(Percentile(searchLatencies, .50), 3),
        p95Milliseconds = Math.Round(Percentile(searchLatencies, .95), 3),
        maxMilliseconds = Math.Round(searchLatencies[^1], 3),
        targetUnder100Milliseconds = Percentile(searchLatencies, .95) < 100
    }
};
// Protect historical artifacts even for direct harness calls or racing wrappers.
await using (var output = new FileStream(arguments["--output"], FileMode.CreateNew, FileAccess.Write))
    await JsonSerializer.SerializeAsync(output, result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

static double Percentile(IReadOnlyList<double> values, double percentile) => values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];
static float[] Unit(int position) { var vector = new float[768]; vector[position] = 1; return vector; }
static void Normalize(float[] vector)
{
    var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
    for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
}

sealed record Fixture(string Scope, List<Topic> Topics);
sealed record Topic(string Key, string Section, string Text, string ExactQuery, string SemanticText, string SemanticQuery);

sealed class Corpus : IDisposable
{
    private static readonly PropertyInfo VectorProperty = typeof(Chunk).GetProperty(nameof(Chunk.Embedding))!;
    private readonly Guid _documentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public Guid WorkspaceId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public AppDbContext Db { get; }
    public EvalColab Colab { get; } = new();
    public AnalysisController Controller { get; }
    public Dictionary<Guid, string> TextById { get; } = [];
    public long StoredVectorBytes { get; private set; }

    public Corpus(string name, string provider)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == "sqlserver")
        {
            // Never use an application connection string/database. Same isolated
            // integrated-auth server selector as the SQL retrieval regressions.
            var connection = new SqlConnectionStringBuilder
            {
                DataSource = Environment.GetEnvironmentVariable("CLINICALRAG_TEST_SQLSERVER") ?? @"(localdb)\MSSQLLocalDB",
                InitialCatalog = $"ClinicalRagRetrievalEval_{Guid.NewGuid():N}", IntegratedSecurity = true,
                TrustServerCertificate = true, MultipleActiveResultSets = true
            };
            options.UseSqlServer(connection.ConnectionString);
        }
        else if (provider == "inmemory") options.UseInMemoryDatabase($"eval-{name}-{Guid.NewGuid():N}");
        else throw new ArgumentException("Unsupported evaluation provider.", nameof(provider));
        Db = new AppDbContext(options.Options);
        try
        {
            if (Db.Database.IsSqlServer()) Db.Database.Migrate();
            var user = new User { FirebaseUid = "eval-only", Email = "synthetic@example.invalid" };
            Db.Users.Add(user); Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Synthetic eval", OwnerId = user.Id });
            Db.Documents.Add(new Document { Id = _documentId, WorkspaceId = WorkspaceId, FileName = "synthetic-only.pdf", GuidelineStatus = "final", Society = "Synthetic fixture" });
            Db.ConfigVersions.Add(new ConfigVersion { WorkspaceId = WorkspaceId, Status = "published", Version = 1, ConfigJson = "{\"retrieval\":{\"topK\":8,\"candidates\":30,\"minRerank\":0}}" });
            Db.SaveChanges();
            Controller = new AnalysisController(Db, Colab, new ConfigService(Db), null!, null!, new ConfigurationBuilder().Build(), NullLogger<AnalysisController>.Instance);
        }
        catch { Dispose(); throw; }
    }

    public Guid Add(int number, string text, string section, float[] vector)
    {
        var id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        var chunk = new Chunk { Id = id, WorkspaceId = WorkspaceId, DocumentId = _documentId, Page = 1, Text = text, Section = section, Status = "embedded", EmbeddingModel = "eval-fixed-vectors-v1" };
        if (VectorProperty.PropertyType == typeof(byte[]))
        {
            var bytes = new byte[vector.Length * sizeof(float)];
            for (var i = 0; i < vector.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), vector[i]);
            VectorProperty.SetValue(chunk, bytes); StoredVectorBytes += bytes.Length;
        }
        else
        {
            var json = JsonSerializer.Serialize(vector); VectorProperty.SetValue(chunk, json); StoredVectorBytes += Encoding.Unicode.GetByteCount(json);
        }
        Db.Chunks.Add(chunk); TextById[id] = text; return id;
    }

    public void Dispose()
    {
        try { if (Db.Database.IsSqlServer()) Db.Database.EnsureDeleted(); }
        finally { Db.Dispose(); }
    }
}

sealed class EvalColab : IColabClient
{
    public float[] Vector { get; set; } = [1];
    public List<string> LastPassages { get; private set; } = [];
    public Task<JsonElement> EmbedAsync(List<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { embeddings = texts.Select(_ => Vector), model = "eval-fixed-vectors-v1", dimensions = Vector.Length }));
    public Task<JsonElement> RerankAsync(string query, List<string> passages, CancellationToken cancellationToken = default)
    {
        LastPassages = passages;
        return Task.FromResult(JsonSerializer.SerializeToElement(new { scores = passages.Select(_ => .9) }));
    }
    public Task<JsonElement> RetrieveAsync(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GenerateAsync(string prompt, int maxNewTokens = 400, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> PostRawAsync(string path, JsonElement body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> HealthAsync() => throw new NotSupportedException();
    public Task<ColabReadiness> ReadyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> ParsePdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement?> RunChunkJobAsync(JsonElement body, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
