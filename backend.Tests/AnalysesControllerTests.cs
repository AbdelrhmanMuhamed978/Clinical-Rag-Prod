using System.Text.Json;
using ClinicalRag.Api.Controllers;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicalRag.Tests;

public class AnalysesControllerTests
{
    [Fact]
    public async Task List_open_and_delete_are_scoped_to_both_user_and_workspace()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SavedAnalyses_{Guid.NewGuid():N}").Options);
        var user = new User { FirebaseUid = "mine", Email = "mine@example.com" };
        var other = new User { FirebaseUid = "other", Email = "other@example.com" };
        db.Users.AddRange(user, other);
        var workspace = Guid.NewGuid();
        var own = new AnalysisRun { WorkspaceId = workspace, UserId = user.Id, InputHash = "own", ResultJson = new FakePipelineClient().Result.GetRawText() };
        var foreign = new AnalysisRun { WorkspaceId = workspace, UserId = other.Id, InputHash = "foreign" };
        var otherWorkspace = new AnalysisRun { WorkspaceId = Guid.NewGuid(), UserId = user.Id, InputHash = "other-workspace" };
        db.AnalysisRuns.AddRange(own, foreign, otherWorkspace);
        await db.SaveChangesAsync();
        var controller = new AnalysesController(db, new FakeCurrentUser(user));

        var listed = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.List(workspace, true)).Value);
        Assert.Single(listed.EnumerateArray());
        Assert.Equal(own.Id, listed[0].GetProperty("Id").GetGuid());
        Assert.IsType<NotFoundResult>(await controller.Open(workspace, foreign.Id));
        Assert.IsType<NotFoundResult>(await controller.Delete(workspace, foreign.Id));
        Assert.IsType<NotFoundResult>(await controller.Open(workspace, otherWorkspace.Id));
        Assert.IsType<NotFoundResult>(await controller.Delete(workspace, otherWorkspace.Id));
        Assert.IsType<BadRequestObjectResult>(await controller.List(workspace, false));
        Assert.IsType<NoContentResult>(await controller.Delete(workspace, own.Id));
        Assert.Equal(2, await db.AnalysisRuns.CountAsync());
    }

    [Fact]
    public async Task Open_never_returns_legacy_free_text_or_patient_facts()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SavedAnalysesPrivacy_{Guid.NewGuid():N}").Options);
        var user = new User { FirebaseUid = "mine", Email = "mine@example.com" };
        var workspace = Guid.NewGuid();
        db.Users.Add(user);
        var legacy = new AnalysisRun { WorkspaceId = workspace, UserId = user.Id, InputHash = "legacy",
            ResultJson = "{\"answer\":\"UNVERIFIED_LEGACY_MARKER\"}" };
        var verified = new AnalysisRun { WorkspaceId = workspace, UserId = user.Id, InputHash = "verified",
            ResultJson = new FakePipelineClient().Result.GetRawText() };
        db.AnalysisRuns.AddRange(legacy, verified);
        await db.SaveChangesAsync();
        var controller = new AnalysesController(db, new FakeCurrentUser(user));

        var legacyOpened = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Open(workspace, legacy.Id)).Value);
        Assert.Equal(JsonValueKind.Null, legacyOpened.GetProperty("result").ValueKind);
        var opened = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Open(workspace, verified.Id)).Value);
        Assert.False(opened.GetProperty("result").TryGetProperty("facts", out _));
        Assert.DoesNotContain("must never be stored", opened.GetRawText());
    }
}
