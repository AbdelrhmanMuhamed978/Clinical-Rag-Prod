using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text.Json;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/tests")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Viewer)]
public class TestsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TestHarnessService _harness;
    private readonly ICurrentUser _currentUser;
    public TestsController(AppDbContext db, TestHarnessService harness, ICurrentUser currentUser)
    { _db = db; _harness = harness; _currentUser = currentUser; }

    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId) =>
        Ok(await _db.TestCases.Where(t => t.WorkspaceId == workspaceId).OrderBy(t => t.Name).ToListAsync());

    [HttpPost]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] TestCase t)
    {
        t.WorkspaceId = workspaceId; t.Id = Guid.NewGuid();
        _db.TestCases.Add(t); await _db.SaveChangesAsync(); return Ok(t);
    }

    [HttpPatch("{id}")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Update(Guid workspaceId, Guid id, [FromBody] TestCase dto)
    {
        var t = await _db.TestCases.FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (t == null) return NotFound();
        if (dto.Name != null) t.Name = dto.Name;
        if (dto.InputText != null) t.InputText = dto.InputText;
        if (dto.ExpectedJson != null) t.ExpectedJson = dto.ExpectedJson;
        await _db.SaveChangesAsync(); return Ok(t);
    }

    [HttpPost("run")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> Run(Guid workspaceId, [FromBody] RunTestsDto dto)
    {
        var actor = await _currentUser.GetUserIdAsync();
        var results = await _harness.RunAsync(workspaceId, dto.CaseIds, dto.ConfigId, actor, HttpContext.RequestAborted);
        return Ok(new { ran = results.Count, results });
    }

    [HttpPost("import-seed")]
    [WorkspaceAuthorize(WorkspaceRole.Editor)]
    public async Task<IActionResult> ImportSeed(Guid workspaceId)
    {
        var seedPath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "tests.json");
        if (!System.IO.File.Exists(seedPath))
            seedPath = System.IO.Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())!.FullName, "tests.json");
        if (!System.IO.File.Exists(seedPath)) return NotFound(new { error = "Seed file not found." });
        var seed = JsonDocument.Parse(System.IO.File.ReadAllText(seedPath));
        var cases = seed.RootElement.GetProperty("cases").EnumerateArray();
        var actor = await _currentUser.GetUserIdAsync();
        var added = 0;
        foreach (var c in cases)
        {
            var id = Guid.NewGuid();
            var name = c.GetProperty("name").GetString()!;
            var input = c.GetProperty("inputText").GetString()!;
            var expected = c.TryGetProperty("expectedJson", out var ej) ? ej.GetRawText() : null;
            var exists = await _db.TestCases.AnyAsync(t => t.WorkspaceId == workspaceId && t.Name == name, HttpContext.RequestAborted);
            if (exists) continue;
            _db.TestCases.Add(new TestCase
            {
                Id = id, WorkspaceId = workspaceId, Name = name, InputText = input, ExpectedJson = expected,
                CreatedAt = DateTime.UtcNow
            });
            added++;
        }
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { importedCount = added });
    }

    [HttpGet("runs/{caseId}")]
    public async Task<IActionResult> RunsForCase(Guid workspaceId, Guid caseId)
    {
        var runs = await _db.TestRuns.Where(r => r.WorkspaceId == workspaceId && r.CaseId == caseId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.Id, r.ConfigVersion, r.Status, r.Diff, r.DurationMilliseconds, r.StartedAt, r.CompletedAt })
            .ToListAsync();
        return Ok(runs);
    }
}

public record RunTestsDto(List<Guid>? CaseIds, Guid? ConfigId);
