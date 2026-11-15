using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class ManagementIntegrityTests
{
    private static WorkspacesController Controller(DocumentTestStore store, AppDbContext db, User? actor = null) =>
        new(db, new FakeCurrentUser(actor ?? store.User), store.Configuration) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static CurrentUser Identity(AppDbContext db, string uid, string email, bool verified, string scheme = "Bearer") => new(
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, uid), new Claim(ClaimTypes.Email, email), new Claim("email_verified", verified ? "true" : "false")], scheme)) } }, db);
    private static async Task<User> AddMember(DocumentTestStore store, string role)
    {
        await using var db = store.CreateContext(); var user = new User { FirebaseUid = Guid.NewGuid().ToString(), Email = $"{Guid.NewGuid():N}@example.org" };
        db.Users.Add(user); db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = store.Workspace.Id, UserId = user.Id, Role = role });
        await db.SaveChangesAsync(); return user;
    }

    [Fact]
    public async Task Old_pending_invitation_never_upgrades_an_existing_member()
    {
        using var store = new DocumentTestStore(); var member = await AddMember(store, "viewer");
        await using var db = store.CreateContext();
        db.WorkspaceInvitations.Add(new() { WorkspaceId = store.Workspace.Id, Email = member.Email, Role = "owner", InvitedBy = store.User.Id }); await db.SaveChangesAsync();
        await Identity(db, member.FirebaseUid, member.Email, true).EnsureUserAsync();
        db.ChangeTracker.Clear(); Assert.Equal("viewer", (await db.WorkspaceMembers.SingleAsync(m => m.UserId == member.Id)).Role);
        Assert.Empty(await db.WorkspaceInvitations.ToListAsync());
    }

    [Fact]
    public async Task Reset_disjoint_pages_keeps_gap_chunks_and_invalid_page_requests_do_not_mutate_state()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync();
        await store.AddChunkAsync(doc, page: 1); var gap = await store.AddChunkAsync(doc, page: 2); await store.AddChunkAsync(doc, page: 3);
        await using var db = store.CreateContext(); var controller = store.Controller(db);
        Assert.IsType<BadRequestObjectResult>(await controller.ResetPages(store.Workspace.Id, doc.Id, new([4])));
        Assert.Equal(3, await db.Chunks.CountAsync());
        Assert.IsType<OkObjectResult>(await controller.ResetPages(store.Workspace.Id, doc.Id, new([1, 3])));
        Assert.Equal(gap.Id, (await db.Chunks.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public async Task Imports_reject_invalid_or_out_of_document_ranges(int start, int end)
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); await using var db = store.CreateContext();
        Assert.IsType<BadRequestObjectResult>(await new ChunkReviewController(db, new FakeCurrentUser(store.User)).BulkCreate(store.Workspace.Id,
            [new() { DocumentId = doc.Id, Page = 1, PageStart = start, PageEnd = end, Text = "Synthetic source." }]));
        Assert.Empty(await db.Chunks.ToListAsync());
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    public async Task Chunking_rejects_upstream_ranges_outside_the_requested_batch_without_replacing_existing_chunks(int start, int end)
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); var retained = await store.AddChunkAsync(doc);
        await using var db = store.CreateContext();
        var mock = new MockColabHandler { RespondAsync = _ => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) {
            Content = JsonContent.Create(new { chunks = new[] { new { page = 1, page_start = start, page_end = end, text = "Synthetic replacement." } } }) }) };
        var result = Assert.IsType<OkObjectResult>(await store.Controller(db, http: new FakeHttpClientFactory(mock))
            .ChunkPortions(store.Workspace.Id, doc.Id, new([1], "Keep ranges.")));
        Assert.Equal(1, Assert.Single(DocumentTestStore.Json(result.Value).GetProperty("failedPages").EnumerateArray()).GetInt32());
        Assert.Equal(retained.Id, (await db.Chunks.SingleAsync()).Id);
        Assert.Equal("[]", (await db.Documents.SingleAsync()).ChunkedPageNumbers);
    }

    [Fact]
    public async Task Pending_invite_is_normalized_and_accepted_only_after_verified_email_sign_in_once()
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext();
        var controller = Controller(store, db);
        Assert.IsType<OkObjectResult>(await controller.Invite(store.Workspace.Id, new("  Invitee@Example.org ", "reviewer")));
        Assert.Equal("invitee@example.org", (await db.WorkspaceInvitations.SingleAsync()).Email);
        Assert.IsType<ConflictObjectResult>(await controller.Invite(store.Workspace.Id, new("invitee@example.org", "editor")));
        var unverified = Identity(db, "invitee", "invitee@example.org", false);
        var user = await unverified.EnsureUserAsync();
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.UserId == user.Id)); Assert.Single(await db.WorkspaceInvitations.AsNoTracking().ToListAsync());
        var signedIn = Identity(db, "invitee", "INVITEE@example.org", true);
        Assert.Equal(user.Id, await signedIn.GetUserIdAsync());
        Assert.Equal(user.Id, await signedIn.GetUserIdAsync());
        db.ChangeTracker.Clear();
        Assert.Equal("reviewer", (await db.WorkspaceMembers.SingleAsync(m => m.UserId == user.Id)).Role);
        Assert.Empty(await db.WorkspaceInvitations.ToListAsync());
        Assert.Equal(1, await db.RuleAuditEntries.CountAsync(a => a.Action == "member_invite_accepted"));
    }

    [Theory]
    [InlineData("other@example.org", "Bearer")]
    [InlineData("invitee@example.org", "Dev")]
    public async Task Wrong_email_or_dev_identity_cannot_accept_invitations(string email, string scheme)
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext();
        await Controller(store, db).Invite(store.Workspace.Id, new("invitee@example.org", "editor"));
        var user = await Identity(db, "untrusted-invitee", email, true, scheme).EnsureUserAsync();
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.UserId == user.Id)); Assert.Single(await db.WorkspaceInvitations.ToListAsync());
    }

    [Fact]
    public async Task Cancelled_invite_never_grants_membership_and_role_removal_and_leave_flow_are_scoped()
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext(); var owner = Controller(store, db);
        await owner.Invite(store.Workspace.Id, new("cancelled@example.org"));
        var invite = await db.WorkspaceInvitations.SingleAsync();
        Assert.IsType<OkObjectResult>(await owner.CancelInvitation(store.Workspace.Id, invite.Id));
        var user = await Identity(db, "cancelled", "cancelled@example.org", true).EnsureUserAsync();
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.UserId == user.Id));
        var member = await AddMember(store, "viewer");
        Assert.IsType<OkObjectResult>(await owner.MemberRole(store.Workspace.Id, member.Id, new("editor", "viewer")));
        Assert.IsType<ConflictObjectResult>(await owner.RemoveMember(store.Workspace.Id, member.Id, "viewer"));
        Assert.IsType<OkObjectResult>(await Controller(store, db, member).Leave(store.Workspace.Id));
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.UserId == member.Id));
        var another = await AddMember(store, "reviewer");
        Assert.IsType<OkObjectResult>(await owner.RemoveMember(store.Workspace.Id, another.Id, "reviewer"));
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync());
    }

    [Fact]
    public async Task Last_owner_cannot_leave_be_removed_or_demoted_and_canonical_owner_tracks_remaining_owner()
    {
        using var store = new DocumentTestStore(); await using var db = store.CreateContext(); var controller = Controller(store, db);
        Assert.IsType<ConflictObjectResult>(await controller.Leave(store.Workspace.Id));
        Assert.IsType<ConflictObjectResult>(await controller.RemoveMember(store.Workspace.Id, store.User.Id, "owner"));
        Assert.IsType<ConflictObjectResult>(await controller.MemberRole(store.Workspace.Id, store.User.Id, new("viewer", "owner")));
        var another = await AddMember(store, "editor");
        Assert.IsType<OkObjectResult>(await controller.MemberRole(store.Workspace.Id, another.Id, new("owner", "editor")));
        Assert.IsType<OkObjectResult>(await controller.Leave(store.Workspace.Id));
        db.ChangeTracker.Clear(); Assert.Equal(another.Id, (await db.Workspaces.SingleAsync()).OwnerId);
        Assert.Equal("owner", (await db.WorkspaceMembers.SingleAsync()).Role);
    }

    [Fact]
    public async Task Shared_revision_rolls_back_stale_owner_removal_then_guards_the_last_owner_after_refresh()
    {
        using var store = new DocumentTestStore(); var second = await AddMember(store, "owner"); var third = await AddMember(store, "owner");
        await using var stale = store.CreateContext(); await stale.Workspaces.SingleAsync();
        await using (var first = store.CreateContext())
            Assert.IsType<OkObjectResult>(await Controller(store, first).RemoveMember(store.Workspace.Id, second.Id, "owner"));
        Assert.IsType<ConflictObjectResult>(await Controller(store, stale).RemoveMember(store.Workspace.Id, third.Id, "owner"));
        await using var fresh = store.CreateContext(); Assert.Equal(2, await fresh.WorkspaceMembers.CountAsync(m => m.Role == "owner"));
        Assert.IsType<OkObjectResult>(await Controller(store, fresh).RemoveMember(store.Workspace.Id, third.Id, "owner"));
        Assert.IsType<ConflictObjectResult>(await Controller(store, fresh).Leave(store.Workspace.Id));
        Assert.Equal(1, await fresh.WorkspaceMembers.CountAsync(m => m.Role == "owner"));
    }

    [Fact]
    public async Task Workspace_rename_checks_snapshot_and_soft_delete_cleans_documents_guidance_files_but_retains_audit()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); await store.AddChunkAsync(doc, "embedded", "[1,0]");
        await using var db = store.CreateContext(); db.ChunkerGuidances.Add(new() { DocumentId = doc.Id, Guidance = "Keep grades." });
        db.RuleAuditEntries.Add(new() { WorkspaceId = store.Workspace.Id, Action = "created" }); await db.SaveChangesAsync();
        var controller = Controller(store, db);
        Assert.IsType<ConflictObjectResult>(await controller.Rename(store.Workspace.Id, new("New name", "wrong")));
        Assert.IsType<OkObjectResult>(await controller.Rename(store.Workspace.Id, new(" New name ", store.Workspace.Name)));
        await controller.Invite(store.Workspace.Id, new("pending@example.org"));
        Assert.IsType<OkObjectResult>(await controller.Delete(store.Workspace.Id));
        db.ChangeTracker.Clear(); Assert.NotNull((await db.Workspaces.SingleAsync()).DeletedAt);
        Assert.Empty(await db.Documents.ToListAsync()); Assert.Empty(await db.Chunks.ToListAsync()); Assert.Empty(await db.ChunkerGuidances.ToListAsync());
        Assert.Empty(await db.WorkspaceInvitations.ToListAsync()); Assert.False(File.Exists(doc.StoragePath));
        Assert.Empty(await db.PendingUploadDeletions.ToListAsync()); Assert.True(await db.RuleAuditEntries.AnyAsync(a => a.Action == "created"));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<Workspace>>(Assert.IsType<OkObjectResult>(await controller.List()).Value));
        Assert.IsType<NotFoundResult>(await controller.Get(store.Workspace.Id));
    }

    [Fact]
    public async Task Workspace_delete_refuses_busy_documents_and_stale_upload_claims_roll_back_cleanup()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync();
        await using var stale = store.CreateContext(); await stale.Workspaces.SingleAsync();
        await using (var writer = store.CreateContext()) { var workspace = await writer.Workspaces.SingleAsync(); workspace.MembershipRevision++; await writer.SaveChangesAsync(); }
        Assert.IsType<ConflictObjectResult>(await Controller(store, stale).Delete(store.Workspace.Id));
        await using var db = store.CreateContext(); Assert.Single(await db.Documents.ToListAsync()); Assert.Null((await db.Workspaces.SingleAsync()).DeletedAt);
        Assert.True(File.Exists(doc.StoragePath)); Assert.Empty(await db.PendingUploadDeletions.ToListAsync());
        DocumentLifecycle.Begin(await db.Documents.SingleAsync(), "embedding"); await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await Controller(store, db).Delete(store.Workspace.Id));
    }

    [Fact]
    public async Task Failed_file_cleanup_is_durable_and_retries_without_deleting_outside_uploads()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); var outside = store.CreateFile(false);
        await using var db = store.CreateContext(); (await db.Documents.SingleAsync()).StoragePath = outside;
        db.ChunkerGuidances.Add(new() { DocumentId = doc.Id, Guidance = "Saved." }); await db.SaveChangesAsync();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db).Delete(store.Workspace.Id, doc.Id));
        Assert.NotNull(DocumentTestStore.Json(response.Value).GetProperty("warning").GetString());
        Assert.Empty(await db.ChunkerGuidances.ToListAsync()); var pending = await db.PendingUploadDeletions.SingleAsync(); Assert.Equal(outside, pending.StoragePath);
        Assert.Equal(1, await StoredDocumentFiles.CompleteAsync(db, store.Configuration, [pending])); Assert.True(File.Exists(outside));
        // Operator corrects a legacy path; the next bounded cleanup pass is idempotent.
        pending.StoragePath = doc.StoragePath!; await db.SaveChangesAsync();
        Assert.Equal(0, await StoredDocumentFiles.CompleteAsync(db, store.Configuration, [pending])); Assert.Empty(await db.PendingUploadDeletions.ToListAsync());
    }

    [Fact]
    public async Task Reset_selected_pages_deletes_overlapping_ranges_clears_all_covered_flags_and_keeps_other_pages_and_guidance()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync("indexed", pageCount: 5);
        var merged = await store.AddChunkAsync(doc, "embedded", "[1,0]", 2); var retained = await store.AddChunkAsync(doc, "embedded", "[0,1]", 4);
        await using var db = store.CreateContext(); var range = await db.Chunks.SingleAsync(c => c.Id == merged.Id); range.PageStart = 1; range.PageEnd = 3;
        var document = await db.Documents.SingleAsync(); document.ChunkedPageNumbers = "[1,2,3,4,5]"; document.ChunkedPagesCount = 5;
        document.EmbeddingTotal = 2; document.EmbeddingCompleted = 2; document.EmbeddingBatchSize = 2;
        db.ChunkerGuidances.Add(new() { DocumentId = doc.Id, Guidance = "Keep ranges." }); await db.SaveChangesAsync();
        var response = Assert.IsType<OkObjectResult>(await store.Controller(db).ResetPages(store.Workspace.Id, doc.Id, new([2])));
        Assert.Equal(new[] { 1, 2, 3 }, DocumentTestStore.Json(response.Value).GetProperty("resetPages").EnumerateArray().Select(p => p.GetInt32()));
        db.ChangeTracker.Clear(); document = await db.Documents.SingleAsync(); Assert.Equal("[4,5]", document.ChunkedPageNumbers); Assert.Equal(2, document.ChunkedPagesCount);
        Assert.Null(document.EmbeddingTotal); Assert.Equal(retained.Id, (await db.Chunks.SingleAsync()).Id); Assert.Single(await db.ChunkerGuidances.ToListAsync()); Assert.True(File.Exists(doc.StoragePath));
    }

    [Fact]
    public async Task Reset_embeddings_preserves_review_attribution_and_flags_and_clears_every_vector_and_embedding_progress()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync("indexed"); var embedded = await store.AddChunkAsync(doc, "embedded", "[1,0]");
        await store.AddChunkAsync(doc, "rejected", "[0,1]", 2);
        await using var db = store.CreateContext(); var original = await db.Chunks.SingleAsync(c => c.Id == embedded.Id); original.ReviewedBy = store.User.Id; original.ReviewedAt = DateTime.UtcNow;
        var document = await db.Documents.SingleAsync(); document.ChunkedPageNumbers = "[1,2]"; document.EmbeddingTotal = 1; document.EmbeddingCompleted = 1; await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await store.Controller(db).ResetEmbeddings(store.Workspace.Id, doc.Id));
        db.ChangeTracker.Clear(); var chunks = await db.Chunks.ToListAsync(); Assert.All(chunks, c => { Assert.Null(c.Embedding); Assert.Null(c.EmbeddingModel); });
        Assert.Equal("accepted", chunks.Single(c => c.Id == embedded.Id).Status); Assert.Equal(store.User.Id, chunks.Single(c => c.Id == embedded.Id).ReviewedBy);
        document = await db.Documents.SingleAsync(); Assert.Equal("review", document.Status); Assert.Equal("[1,2]", document.ChunkedPageNumbers); Assert.Null(document.EmbeddingTotal); Assert.Equal(0, document.EmbeddingCompleted);
    }

    [Theory]
    [InlineData("reset-pages")]
    [InlineData("reset-embeddings")]
    public async Task Stale_resets_cannot_mutate_chunks_after_an_operation_claim(string action)
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync("indexed"); var chunk = await store.AddChunkAsync(doc, "embedded", "[1,0]");
        await using var stale = store.CreateContext(); await stale.Documents.SingleAsync();
        await using (var active = store.CreateContext()) { DocumentLifecycle.Begin(await active.Documents.SingleAsync(), "embedding"); await active.SaveChangesAsync(); }
        var controller = store.Controller(stale);
        Assert.IsType<ConflictObjectResult>(action == "reset-pages" ? await controller.ResetPages(store.Workspace.Id, doc.Id, new([1])) : await controller.ResetEmbeddings(store.Workspace.Id, doc.Id));
        await using var observe = store.CreateContext(); Assert.Equal(chunk.Embedding, (await observe.Chunks.SingleAsync()).Embedding); Assert.Equal("embedding", (await observe.Documents.SingleAsync()).ProcessingOperation);
    }

    [Fact]
    public async Task Guidance_is_saved_for_noop_and_failed_chunk_calls_and_last_guidance_is_scoped()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); await using var db = store.CreateContext();
        var document = await db.Documents.SingleAsync(); document.ChunkedPageNumbers = "[1]"; await db.SaveChangesAsync();
        var controller = store.Controller(db);
        Assert.IsType<OkObjectResult>(await controller.ChunkPortions(store.Workspace.Id, doc.Id, new([1], "Preserve tables.")));
        Assert.Single(await db.ChunkerGuidances.ToListAsync());
        await controller.ChunkPortions(store.Workspace.Id, doc.Id, new([2], "Keep grades too."));
        Assert.Equal(2, await db.ChunkerGuidances.CountAsync());
        Assert.Equal("Keep grades too.", DocumentTestStore.Json(Assert.IsType<OkObjectResult>(await controller.Guidance(store.Workspace.Id, doc.Id)).Value).GetProperty("guidance").GetString());
        Assert.IsType<NotFoundResult>(await controller.Guidance(Guid.NewGuid(), doc.Id));
    }

    [Fact]
    public async Task Adjacent_merge_preserves_page_range_year_sections_and_strongest_grades_and_attribution()
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync("review", pageCount: 5);
        var first = await store.AddChunkAsync(doc, "embedded", "[1,0]", 2); var second = await store.AddChunkAsync(doc, "accepted", page: 4);
        await using var db = store.CreateContext(); var a = await db.Chunks.SingleAsync(c => c.Id == first.Id); var b = await db.Chunks.SingleAsync(c => c.Id == second.Id);
        a.PageStart = 1; a.PageEnd = 3; a.Year = 2024; a.Section = "Care"; a.SectionHierarchy = "Management > Care"; a.Strength = "weak"; a.Certainty = "high";
        b.PageStart = 4; b.PageEnd = 5; b.Year = 2024; b.Section = "Follow-up"; b.SectionHierarchy = "Management > Follow-up"; b.Strength = "strong"; b.Certainty = "low";
        await db.SaveChangesAsync();
        var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
        var result = Assert.IsType<OkObjectResult>(await review.Merge(store.Workspace.Id, new([second.Id, first.Id])));
        var id = DocumentTestStore.Json(result.Value).GetProperty("id").GetGuid(); db.ChangeTracker.Clear(); var merged = await db.Chunks.SingleAsync(c => c.Id == id);
        Assert.Equal(1, merged.PageStart); Assert.Equal(5, merged.PageEnd); Assert.Equal(1, merged.Page); Assert.Equal(2024, merged.Year);
        Assert.Equal("strong", merged.Strength); Assert.Equal("high", merged.Certainty); Assert.Equal("Care | Follow-up", merged.Section); Assert.Contains("Management > Follow-up", merged.SectionHierarchy);
        Assert.Equal(first.Text + "\n\n" + second.Text, merged.Text); Assert.Equal(store.User.Id, merged.ReviewedBy); Assert.NotNull(merged.ReviewedAt); Assert.Null(merged.Embedding);
        Assert.All(await db.Chunks.Where(c => c.Id != id).ToListAsync(), c => { Assert.Equal("rejected", c.Status); Assert.Null(c.Embedding); Assert.Equal(store.User.Id, c.ReviewedBy); });
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("document")]
    [InlineData("year")]
    public async Task Merge_rejects_gaps_foreign_documents_and_conflicting_years_without_mutation(string failure)
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync(); var other = failure == "document" ? await store.AddDocumentAsync() : doc;
        var a = await store.AddChunkAsync(doc); var b = await store.AddChunkAsync(other, page: failure == "gap" ? 3 : 2);
        await using var db = store.CreateContext(); if (failure == "year") { (await db.Chunks.SingleAsync(c => c.Id == a.Id)).Year = 2024; (await db.Chunks.SingleAsync(c => c.Id == b.Id)).Year = 2025; await db.SaveChangesAsync(); }
        Assert.IsType<BadRequestObjectResult>(await new ChunkReviewController(db, new FakeCurrentUser(store.User)).Merge(store.Workspace.Id, new([a.Id, b.Id])));
        Assert.Equal(2, await db.Chunks.CountAsync()); Assert.All(await db.Chunks.ToListAsync(), c => Assert.Equal("pending", c.Status));
    }

    [Theory]
    [InlineData("accepted", "text")]
    [InlineData("embedded", "text")]
    [InlineData("accepted", "section")]
    [InlineData("embedded", "section")]
    public async Task Reviewed_edits_force_accepted_clear_stale_vectors_and_record_authenticated_reviewer(string status, string field)
    {
        using var store = new DocumentTestStore(); var doc = await store.AddDocumentAsync("indexed"); var chunk = await store.AddChunkAsync(doc, status, "[1,0]");
        await using var db = store.CreateContext(); var review = new ChunkReviewController(db, new FakeCurrentUser(store.User));
        Assert.IsType<BadRequestObjectResult>(await review.Review(store.Workspace.Id, chunk.Id, new("invented", null, null)));
        Assert.IsType<OkObjectResult>(await review.Review(store.Workspace.Id, chunk.Id, new("pending", field == "text" ? "Changed source." : null, field == "section" ? "Changed section" : null)));
        db.ChangeTracker.Clear(); var edited = await db.Chunks.SingleAsync(); Assert.Equal("accepted", edited.Status); Assert.Null(edited.Embedding); Assert.Null(edited.EmbeddingModel); Assert.Null(edited.LegacyEmbeddingJson);
        Assert.Equal(store.User.Id, edited.ReviewedBy); Assert.NotNull(edited.ReviewedAt); Assert.Equal("review", (await db.Documents.SingleAsync()).Status);
    }
}
