using System.Globalization;
using System.Text;
using ClinicalRag.Api.Attributes;
using ClinicalRag.Api.Data;
using ClinicalRag.Api.Models;
using ClinicalRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalRag.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId}/governance")]
[Authorize]
[WorkspaceAuthorize(WorkspaceRole.Owner)]
public class GovernanceController : ControllerBase
{
    private readonly AppDbContext _db;
    public GovernanceController(AppDbContext db) => _db = db;

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(Guid workspaceId, [FromQuery] string format = "csv",
        CancellationToken cancellationToken = default)
    {
        var rules = await _db.Rules.AsNoTracking()
            .Where(rule => rule.WorkspaceId == workspaceId && rule.ReviewedAt != null)
            .OrderBy(rule => rule.ReviewedAt).ThenBy(rule => rule.RuleId)
            .Select(rule => new
            {
                rule.RuleId, rule.Title, rule.ReviewerName, rule.ApprovedSourceUrl,
                rule.ReviewedAt, rule.Status
            }).ToListAsync(cancellationToken);
        var publications = await _db.ConfigVersions.AsNoTracking()
            .Where(version => version.WorkspaceId == workspaceId && version.PublishedAt != null)
            .OrderBy(version => version.PublishedAt).ThenBy(version => version.Version)
            .Select(version => new { version.Version, version.Status, version.PublishedAt })
            .ToListAsync(cancellationToken);

        var rows = new List<string[]> { new[] { "type", "key", "title", "reviewer_or_version", "approved_or_published_at", "source_url", "status" } };
        rows.AddRange(rules.Select(rule => new[]
        {
            "rule", rule.RuleId, rule.Title, rule.ReviewerName ?? "not recorded",
            rule.ReviewedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "not recorded",
            rule.ApprovedSourceUrl ?? "not recorded", rule.Status
        }));
        rows.AddRange(publications.Select(version => new[]
        {
            "config", $"v{version.Version}", "Configuration publication", $"v{version.Version}",
            version.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "not recorded",
            "", version.Status
        }));

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(Csv))) ) + "\r\n";
            return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
                $"governance-audit-{workspaceId:N}.csv");
        }
        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            var lines = new List<string> { "Clinical RAG governance audit", $"Workspace: {workspaceId}" };
            lines.AddRange(rules.Select(rule =>
                $"Rule {rule.RuleId} approved {rule.ReviewedAt:u} by {rule.ReviewerName ?? "not recorded"} source {rule.ApprovedSourceUrl ?? "not recorded"} status {rule.Status}"));
            lines.AddRange(publications.Select(version =>
                $"Configuration v{version.Version} published {version.PublishedAt:u} status {version.Status}"));
            return File(SimplePdf.Build(lines), "application/pdf", $"governance-audit-{workspaceId:N}.pdf");
        }
        return BadRequest(new { error = "Choose csv or pdf." });
    }

    private static string Csv(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

internal static class SimplePdf
{
    public static byte[] Build(IReadOnlyList<string> lines)
    {
        static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)
            .Select(character => character is >= ' ' and <= '~' ? character : '?').Aggregate(new StringBuilder(),
                (builder, character) => builder.Append(character)).ToString();
        var content = new StringBuilder("BT /F1 9 Tf 40 800 Td 12 TL\n");
        foreach (var line in lines) content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        content.Append("ET");
        var stream = Encoding.ASCII.GetBytes(content.ToString());
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            writer.Flush();
            offsets.Add(output.Position);
            writer.Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        writer.Flush();
        var xref = output.Position;
        writer.Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) writer.Write($"{offset:0000000000} 00000 n \n");
        writer.Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return output.ToArray();
    }
}
