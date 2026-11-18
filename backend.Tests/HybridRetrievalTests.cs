using System.Text;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClinicalRag.Tests;

public class HybridRetrievalTests
{
    private static AnalysisController Controller(DocumentTestStore store, ClinicalRag.Api.Data.AppDbContext db, FakeColabClient client) =>
        new(db, client, new ConfigService(db), new FakePipelineClient(), new FakeCurrentUser(store.User), store.Configuration, NullLogger<AnalysisController>.Instance);

    private static async Task<Chunk> Indexed(DocumentTestStore store, Document document, string text = "Synthetic source text", string? section = null, float x = 1, float y = 0, string? metadata = null)
    {
        var chunk = new Chunk { WorkspaceId = document.WorkspaceId, DocumentId = document.Id, Text = text, Section = section, Status = "embedded", Page = 1, Metadata = metadata };
        EmbeddingTestData.Set(chunk, x, y);
        await using var db = store.CreateContext(); db.Chunks.Add(chunk); await db.SaveChangesAsync(); return chunk;
    }

    private static FakeColabClient Client(string model = EmbeddingTestData.Model) => new()
    { EmbedHandler = texts => Task.FromResult(EmbeddingTestData.Result(texts.Count, model, 1, 0)) };

    [Fact]
    public async Task Query_embedding_from_a_different_model_is_refused_before_reranking()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await Indexed(store, document);
        await using var db = store.CreateContext(); var client = Client("different-model"); var reranked = false;
        client.RerankHandler = (_, _) => { reranked = true; throw new InvalidOperationException("must not rerank mixed spaces"); };
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Controller(store, db, client).DoRetrieve(store.Workspace.Id, "source"));
        Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase); Assert.False(reranked);
    }

    [Fact]
    public async Task A_workspace_with_mixed_embedding_models_is_refused_not_silently_filtered()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await Indexed(store, document);
        var other = await Indexed(store, document, "Other source");
        await using var db = store.CreateContext(); (await db.Chunks.SingleAsync(c => c.Id == other.Id)).EmbeddingModel = "different-model"; await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Controller(store, db, Client()).DoRetrieve(store.Workspace.Id, "source"));
    }

    [Fact]
    public async Task An_unlabelled_query_embedding_is_refused()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await Indexed(store, document);
        await using var db = store.CreateContext(); var client = new FakeColabClient
        { EmbedHandler = texts => Task.FromResult(JsonSerializer.SerializeToElement(new { embeddings = texts.Select(_ => new[] { 1, 0 }) })) };
        await Assert.ThrowsAsync<InvalidDataException>(() => Controller(store, db, client).DoRetrieve(store.Workspace.Id, "source"));
    }

    [Fact]
    public async Task Lexical_section_matches_rescue_a_chunk_with_zero_dense_similarity()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var target = await Indexed(store, document, "A retained synthetic quotation.", "cystatin monitoring", 0, 1);
        await using var db = store.CreateContext();
        var result = JsonSerializer.SerializeToElement(await Controller(store, db, Client()).DoRetrieve(store.Workspace.Id, "cystatin"));
        Assert.Equal(target.Id.ToString(), Assert.Single(result.GetProperty("chunks").EnumerateArray()).GetProperty("chunk_id").GetString());
    }

    [Fact]
    public async Task Warm_retrieval_excludes_edited_deleted_and_rejected_chunks_and_reads_current_metadata()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var edited = await Indexed(store, document, "Synthetic source edited");
        var deleted = await Indexed(store, document, "Synthetic source deleted");
        var rejected = await Indexed(store, document, "Synthetic source rejected");
        var retained = await Indexed(store, document, "Synthetic source retained");
        await using var db = store.CreateContext(); var controller = Controller(store, db, Client());
        Assert.Equal(4, JsonSerializer.SerializeToElement(await controller.DoRetrieve(store.Workspace.Id, "source"))
            .GetProperty("chunks").GetArrayLength());
        // Simulate external writes, deliberately without cache invalidation.
        // Cache IDs/ranks may be old; only current eligible evidence may return.
        await using (var update = store.CreateContext())
        {
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE Chunks SET Text = {"Changed source"}, Embedding = NULL, Status = {"accepted"} WHERE Id = {edited.Id}");
            await update.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Chunks WHERE Id = {deleted.Id}");
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE Chunks SET Status = {"rejected"} WHERE Id = {rejected.Id}");
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE Documents SET Society = {"Current society"}, Year = {2026}, GuidelineStatus = {"final"} WHERE Id = {document.Id}");
        }
        var result = JsonSerializer.SerializeToElement(await controller.DoRetrieve(store.Workspace.Id, "source"));
        var source = Assert.Single(result.GetProperty("chunks").EnumerateArray());
        Assert.Equal(retained.Id.ToString(), source.GetProperty("chunk_id").GetString());
        Assert.Equal("Current society", source.GetProperty("society").GetString());
        Assert.Equal("2026", source.GetProperty("year").GetString());
        Assert.Equal("final", source.GetProperty("status").GetString());
        Assert.Empty(db.ChangeTracker.Entries<Chunk>());
    }

    [Fact]
    public async Task Chunks_marked_for_re_review_are_not_retrieved_even_if_status_and_vector_remain()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var flagged = await Indexed(store, document, "Synthetic source flagged for review");
        await using var db = store.CreateContext();
        await using (var update = store.CreateContext())
            await update.Database.ExecuteSqlInterpolatedAsync($"UPDATE Chunks SET ReReviewRequired = 1 WHERE Id = {flagged.Id}");
        var result = JsonSerializer.SerializeToElement(await Controller(store, db, Client()).DoRetrieve(store.Workspace.Id, "source"));
        Assert.Empty(result.GetProperty("chunks").EnumerateArray());
    }

    [Fact]
    public async Task Retrieval_includes_only_page_footnote_markers_for_quote_canonicalization()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        await Indexed(store, document, "Synthetic source with a known marker.");
        await using (var update = store.CreateContext())
        {
            var saved = await update.Documents.SingleAsync(item => item.Id == document.Id);
            saved.FootnoteMarkers = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new { page = 1, offset = 9, marker = "c", context = "Screening for synthetic source" },
                new { page = 2, offset = 4, marker = "a", context = "Other page" }
            });
            await update.SaveChangesAsync();
        }
        await using var db = store.CreateContext();
        var result = JsonSerializer.SerializeToElement(await Controller(store, db, Client()).DoRetrieve(store.Workspace.Id, "source"));
        var markers = Assert.Single(result.GetProperty("chunks").EnumerateArray()).GetProperty("footnote_markers");
        var marker = Assert.Single(markers.EnumerateArray());
        Assert.Equal("c", marker.GetProperty("marker").GetString());
        Assert.Equal(9, marker.GetProperty("offset").GetInt32());
        Assert.Equal("Screening for synthetic source", marker.GetProperty("context").GetString());
    }

    [Fact]
    public async Task Configured_candidates_above_thirty_never_expand_the_rerank_batch()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        for (var i = 0; i < 40; i++) await Indexed(store, document, $"Synthetic source {i}");
        await using var db = store.CreateContext(); db.ConfigVersions.Add(new ConfigVersion
        { WorkspaceId = store.Workspace.Id, Version = 1, Status = "published", ConfigJson = "{\"retrieval\":{\"candidates\":100}}" }); await db.SaveChangesAsync();
        var client = Client(); var passages = 0;
        client.RerankHandler = (_, texts) => { passages = texts.Count; return Task.FromResult(JsonSerializer.SerializeToElement(new { scores = texts.Select(_ => .9) })); };
        await Controller(store, db, client).DoRetrieve(store.Workspace.Id, "source");
        Assert.Equal(30, passages);
    }

    [Fact]
    public async Task Broad_question_penalizes_a_narrow_exception_after_equal_cross_encoder_scores()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var general = await Indexed(store, document, "General adult ART guidance.",
            metadata: "{\"kind\":\"recommendation\",\"population\":\"adults\"}");
        var narrow = await Indexed(store, document, "TB-specific ART timing exception.",
            metadata: "{\"kind\":\"recommendation\",\"population\":\"tuberculosis\"}");
        await using var db = store.CreateContext(); var client = Client();
        client.RerankHandler = (_, texts) => Task.FromResult(JsonSerializer.SerializeToElement(
            new { scores = texts.Select(_ => 0.9) }));
        var result = JsonSerializer.SerializeToElement(await Controller(store, db, client)
            .DoRetrieve(store.Workspace.Id, "starting ART in adults with HIV"));
        var ids = result.GetProperty("chunks").EnumerateArray()
            .Select(chunk => chunk.GetProperty("chunk_id").GetString()).ToList();
        Assert.Equal(general.Id.ToString(), ids.First());
        Assert.Contains(narrow.Id.ToString(), ids);
    }

    [Fact]
    public async Task Rerank_uses_section_and_the_same_two_thousand_unicode_characters_as_colab()
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync();
        var original = string.Concat(Enumerable.Repeat("😀", 2100)) + "Do not alter the returned quotation.";
        await Indexed(store, document, original, "Section"); await using var db = store.CreateContext(); var client = Client(); string? sent = null;
        client.RerankHandler = (_, texts) => { sent = Assert.Single(texts); return Task.FromResult(JsonSerializer.SerializeToElement(new { scores = new[] { .9 } })); };
        var result = JsonSerializer.SerializeToElement(await Controller(store, db, client).DoRetrieve(store.Workspace.Id, "source"));
        Assert.StartsWith("Section\n", sent); Assert.Equal(2000, sent!.EnumerateRunes().Count()); Assert.DoesNotContain('\uFFFD', sent);
        Assert.Equal(original, Assert.Single(result.GetProperty("chunks").EnumerateArray()).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"scores\":[]}")]
    [InlineData("{\"scores\":[0.9,0.8]}")]
    [InlineData("{\"scores\":[1e400]}")]
    [InlineData("{\"scores\":[-0.1]}")]
    public async Task Malformed_reranker_results_are_refused_instead_of_truncated_or_used(string json)
    {
        using var store = new DocumentTestStore(); var document = await store.AddDocumentAsync(); await Indexed(store, document);
        await using var db = store.CreateContext(); var client = Client(); client.RerankHandler = (_, _) => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        await Assert.ThrowsAsync<InvalidDataException>(() => Controller(store, db, client).DoRetrieve(store.Workspace.Id, "source"));
    }
}
