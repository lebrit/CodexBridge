using CodexBridge.Core;

namespace CodexBridge.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> junctions = [];

    [Theory]
    [InlineData("", false, "", false)]
    [InlineData("local", true, "", false)]
    [InlineData("local", true, "  ", false)]
    [InlineData("local", false, "", true)]
    [InlineData("local", true, "rclone:remote:backup", true)]
    public void Backup_requires_an_address_for_every_enabled_storage(string local, bool enabled, string cloud, bool valid)
    {
        var settings = new AppSettings { LocalRepository = local, CloudEnabled = enabled, CloudRepository = cloud };
        Assert.Equal(valid, BackupCoordinator.ValidateStorageSettings(settings).Succeeded);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Backup_does_not_hide_incomplete_environment_capture(bool cloud, bool captured)
    {
        var capture = captured ? OperationResult.Ok("captured") : OperationResult.Fail("incomplete", "inventory error");
        var result = BackupCoordinator.DescribeCompletedBackup(cloud, capture, "local snapshot");
        Assert.Equal(captured, result.Succeeded);
        Assert.Equal(cloud, result.Message.Contains("облачная"));
        if (!captured)
            Assert.Contains("inventory error", result.Details);
    }

    [Theory]
    [InlineData("..", "Project")]
    [InlineData(". ", "Project")]
    [InlineData(null, "Project")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("LPT²", "_LPT²")]
    [InlineData("project. ", "project")]
    [InlineData("a/b", "a_b")]
    [InlineData("Мой проект", "Мой проект")]
    public void Restored_project_name_is_one_safe_directory(string? input, string expected)
    {
        var name = PathPolicy.SafeDirectoryName(input);
        Assert.Equal(expected, name);
        Assert.Equal(root, Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, name))));
    }

    [Fact]
    public async Task Concurrent_json_saves_and_reads_keep_complete_documents()
    {
        var path = Path.Combine(root, "settings.json");
        var payload = new string('x', 100_000);
        await new JsonFileStore().SaveAsync(path, new Document(0, payload));
        var writers = Enumerable.Range(1, 16).Select(async id =>
        {
            var store = new JsonFileStore();
            await store.SaveAsync(path, new Document(id, payload));
            var loaded = await store.LoadAsync<Document>(path, () => throw new InvalidOperationException());
            Assert.Equal(payload, loaded.Payload);
        });
        await Task.WhenAll(writers);
        var final = await new JsonFileStore().LoadAsync<Document>(path, () => throw new InvalidOperationException());
        Assert.InRange(final.Id, 1, 16);
        Assert.Equal(payload, final.Payload);
        Assert.Single(Directory.EnumerateFiles(root));
    }

    [Fact]
    public async Task Cancelled_json_save_preserves_previous_document_and_cleans_temporary_file()
    {
        var path = Path.Combine(root, "settings.json");
        var store = new JsonFileStore();
        await store.SaveAsync(path, new Document(1, "original"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(path, new Document(2, new string('x', 100_000)), cancellation.Token));
        Assert.Equal(new Document(1, "original"), await store.LoadAsync<Document>(path, () => throw new InvalidOperationException()));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    [Fact]
    public async Task Blocked_json_save_preserves_previous_document_and_cleans_temporary_file()
    {
        var path = Path.Combine(root, "settings.json");
        var store = new JsonFileStore();
        await store.SaveAsync(path, new Document(1, "original"));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync(path, new Document(2, "replacement")));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal(new Document(1, "original"), await store.LoadAsync<Document>(path, () => throw new InvalidOperationException()));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_and_preview_reject_destination_junction_without_writing_outside(bool nested)
    {
        var source = MakeDirectory("source");
        var outside = MakeDirectory("outside");
        var destination = Path.Combine(root, "destination");
        await MakeJunctionAsync(nested ? "destination/child" : "destination", outside);
        var sourceFiles = nested ? Directory.CreateDirectory(Path.Combine(source, "child")).FullName : source;
        await File.WriteAllTextAsync(Path.Combine(sourceFiles, "new.txt"), "incoming");
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeMergeService.PlanDirectoryAsync(source, destination));
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeMergeService.MergeDirectoryAsync(source, destination, Path.Combine(root, "conflicts")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Merge_rejects_source_ancestor_junction()
    {
        var outside = MakeDirectory("outside");
        var child = Directory.CreateDirectory(Path.Combine(outside, "child")).FullName;
        await File.WriteAllTextAsync(Path.Combine(child, "private.txt"), "must not read");
        var link = await MakeJunctionAsync("source-link", outside);
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeMergeService.MergeDirectoryAsync(
            Path.Combine(link, "child"), Path.Combine(root, "destination"), Path.Combine(root, "conflicts")));
        Assert.False(Directory.Exists(Path.Combine(root, "destination")));
    }

    [Fact]
    public async Task Merge_rejects_conflict_junction()
    {
        var source = MakeDirectory("source");
        var destination = MakeDirectory("destination");
        var outside = MakeDirectory("outside");
        var link = await MakeJunctionAsync("conflicts", outside);
        await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "incoming");
        await File.WriteAllTextAsync(Path.Combine(destination, "same.txt"), "existing");
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeMergeService.MergeDirectoryAsync(source, destination, link));
        Assert.Empty(Directory.EnumerateFiles(outside));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "same.txt")));
    }

    [Fact]
    public async Task Rollback_does_not_delete_matching_external_file_after_parent_becomes_junction()
    {
        var source = MakeDirectory("source");
        var destination = MakeDirectory("destination");
        Directory.CreateDirectory(Path.Combine(source, "child"));
        await File.WriteAllTextAsync(Path.Combine(source, "child", "new.txt"), "restored");
        await File.WriteAllTextAsync(Path.Combine(source, "safe.txt"), "safe");
        var store = new RestoreTransactionStore(Path.Combine(root, "journals"));
        var transaction = await store.BeginAsync("snapshot", destination);
        var merged = await SafeMergeService.MergeDirectoryAsync(source, destination, transaction.ConflictRoot, transaction);
        await store.CompleteAsync(transaction, merged);
        Directory.Move(Path.Combine(destination, "child"), Path.Combine(root, "original-child"));
        var outside = MakeDirectory("outside");
        var externalFile = Path.Combine(outside, "new.txt");
        await File.WriteAllTextAsync(externalFile, "restored");
        var externalPartial = $"{externalFile}.codexbridge-{transaction.Id}.partial";
        await File.WriteAllTextAsync(externalPartial, "external partial");
        await MakeJunctionAsync("destination/child", outside);

        var result = await store.RollbackAsync(transaction.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("restored", await File.ReadAllTextAsync(externalFile));
        Assert.Equal("external partial", await File.ReadAllTextAsync(externalPartial));
        Assert.False(File.Exists(Path.Combine(destination, "safe.txt")));
        Assert.Equal(RestoreTransactionStatus.RollbackBlocked, Assert.Single(await store.ListAsync()).Status);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".. ")]
    public void Transaction_id_cannot_escape_journal_directory(string id)
    {
        var store = new RestoreTransactionStore(Path.Combine(root, "journals"));
        Assert.Throws<InvalidDataException>(() => store.GetTransactionDirectory(id));
    }

    [Fact]
    public async Task Transaction_listing_ignores_redirected_journals()
    {
        var store = new RestoreTransactionStore(Path.Combine(root, "journals"));
        var transaction = await store.BeginAsync("snapshot", Path.Combine(root, "destination"));
        var external = Path.Combine(root, "external-journal");
        Directory.Move(store.GetTransactionDirectory(transaction.Id), external);
        await MakeJunctionAsync($"journals/{transaction.Id}", external);
        Assert.Empty(await store.ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RollbackAsync(transaction.Id));
        Assert.True(File.Exists(Path.Combine(external, "transaction.json")));
    }

    private string MakeDirectory(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private async Task<string> MakeJunctionAsync(string name, string target)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = await new ProcessRunner().RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command",
                "New-Item -ItemType Junction -Path $env:CODEXBRIDGE_TEST_LINK -Target $env:CODEXBRIDGE_TEST_TARGET -ErrorAction Stop | Out-Null"],
            new Dictionary<string, string>
            {
                ["CODEXBRIDGE_TEST_LINK"] = path,
                ["CODEXBRIDGE_TEST_TARGET"] = target
            }, workingDirectory: root);
        Assert.True(result.Succeeded, result.Combined);
        junctions.Add(path);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        return path;
    }

    public void Dispose()
    {
        foreach (var junction in junctions)
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    public sealed record Document(int Id, string Payload);
}
