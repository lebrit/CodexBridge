using CodexBridge.Core;

namespace CodexBridge.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ReduceNestedRoots_keeps_only_outer_paths()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexBridge-root"));
        var child = Path.Combine(root, "child");
        var sibling = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexBridge-sibling"));

        var result = PathPolicy.ReduceNestedRoots([child, root, sibling, root]);

        Assert.Equal(2, result.Count);
        Assert.Contains(root, result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(sibling, result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeMerge_preserves_existing_file_and_writes_conflict()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var destination = Path.Combine(testRoot, "destination");
        var conflicts = Path.Combine(testRoot, "conflicts");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(destination, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(source, "different.txt"), "incoming");
        await File.WriteAllTextAsync(Path.Combine(destination, "different.txt"), "existing");
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new");

        try
        {
            var result = await SafeMergeService.MergeDirectoryAsync(source, destination, conflicts);

            Assert.Equal(new MergeResult(1, 1, 1, 0), result);
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "different.txt")));
            Assert.Equal("incoming", await File.ReadAllTextAsync(Path.Combine(conflicts, "different.txt")));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "new.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public void IsInside_rejects_similar_prefix()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexBridge-data"));
        var sibling = root + "-old";

        Assert.False(PathPolicy.IsInside(sibling, root));
        Assert.True(PathPolicy.IsInside(Path.Combine(root, "project"), root));
    }
}
