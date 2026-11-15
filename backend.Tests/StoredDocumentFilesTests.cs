using System.Diagnostics;
using ClinicalRag.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClinicalRag.Tests;

public class StoredDocumentFilesTests
{
    [Fact]
    public void Cleanup_deletes_only_regular_files_inside_uploads_and_is_idempotent_for_missing_files()
    {
        using var store = new DocumentTestStore();
        var inside = store.CreateFile(); var outside = store.CreateFile(false);
        Assert.False(StoredDocumentFiles.TryDelete(store.Configuration, outside));
        Assert.False(StoredDocumentFiles.TryDelete(store.Configuration, store.Uploads));
        Assert.False(StoredDocumentFiles.TryDelete(store.Configuration, Path.Combine(store.Uploads, "..", Path.GetFileName(outside))));
        Assert.True(File.Exists(outside)); Assert.True(Directory.Exists(store.Uploads));
        Assert.True(StoredDocumentFiles.TryDelete(store.Configuration, inside));
        Assert.False(File.Exists(inside)); Assert.True(StoredDocumentFiles.TryDelete(store.Configuration, inside));
    }

    [Fact]
    public void Cleanup_rejects_a_link_ancestor_above_the_configured_upload_root()
    {
        using var store = new DocumentTestStore();
        var nested = Path.Combine(store.Uploads, "nested"); Directory.CreateDirectory(nested);
        var target = Path.Combine(nested, "synthetic.pdf"); File.WriteAllText(target, "Synthetic source.");
        var link = Path.Combine(store.Root, "linked-uploads");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A directory junction is a reparse point and needs no symlink privilege.
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/c", "mklink", "/J", link, store.Uploads }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.True(process.ExitCode == 0, output);
            }
            else Directory.CreateSymbolicLink(link, store.Uploads);
            var root = Path.Combine(link, "nested");
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Documents:UploadsDirectory"] = root }).Build();
            Assert.False(StoredDocumentFiles.TryDelete(cfg, Path.Combine(root, "synthetic.pdf")));
            Assert.True(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            File.Delete(target); Directory.Delete(nested);
        }
    }
}
