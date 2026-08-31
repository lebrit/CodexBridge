using CodexBridge.Core;

namespace CodexBridge.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ProjectCatalogFilter_matches_name_path_and_status()
    {
        var project = new ProjectEntry
        {
            Name = "МФЦ",
            Path = @"C:\Projects\mfc-service",
            IsProtected = true,
            Status = ProjectStatus.Protected
        };

        Assert.True(ProjectCatalogFilter.Matches(project, "мфц", ProjectListFilter.All));
        Assert.True(ProjectCatalogFilter.Matches(project, "MFC-SERVICE", ProjectListFilter.Protected));
        Assert.False(ProjectCatalogFilter.Matches(project, "МФЦ", ProjectListFilter.Excluded));
        Assert.False(ProjectCatalogFilter.Matches(project, "другой", ProjectListFilter.All));
    }

    [Fact]
    public void RecordActivity_keeps_the_ten_newest_entries()
    {
        var state = new BackupState();
        var start = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 12; index++)
            state.RecordActivity(index % 2 == 0, $"Операция {index}", start.AddMinutes(index));

        Assert.Equal(10, state.RecentActivities.Count);
        Assert.Equal("Операция 11", state.RecentActivities[0].Message);
        Assert.Equal("Операция 2", state.RecentActivities[^1].Message);
    }

    [Fact]
    public void RecordRun_marks_automatic_result_for_the_dashboard()
    {
        var timestamp = new DateTimeOffset(2026, 8, 30, 6, 0, 0, TimeSpan.Zero);
        var state = new BackupState();

        state.RecordRun(true, "Локальная копия готова.", BackupRunSource.Automatic, timestamp);

        Assert.Equal(timestamp, state.LastRunUtc);
        Assert.Equal(BackupRunSource.Automatic, state.LastRunSource);
        Assert.True(state.LastRunSucceeded);
        Assert.Equal("Автоматически: Локальная копия готова.", state.RecentActivities[0].Message);
    }

    [Fact]
    public void RecordRestoreTest_keeps_a_persistent_result()
    {
        var timestamp = new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
        var state = new BackupState();

        state.RecordRestoreTest(false, "Снимок повреждён.", timestamp, "abc123");

        Assert.Equal(timestamp, state.LastRestoreTestUtc);
        Assert.False(state.LastRestoreTestSucceeded);
        Assert.Equal("abc123", state.LastRestoreTestSnapshotId);
        Assert.Equal("Снимок повреждён.", state.LastRestoreTestMessage);
        Assert.Equal("Проверка восстановления: Снимок повреждён.", state.RecentActivities[0].Message);
    }

    [Fact]
    public void Scheduler_reads_agent_path_from_task_xml()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><Exec><Command>"C:\Apps\CodexBridge.Agent.exe"</Command></Exec></Actions>
            </Task>
            """;

        Assert.Equal(@"C:\Apps\CodexBridge.Agent.exe", SchedulerService.ReadAgentExecutable(xml));
        Assert.Null(SchedulerService.ReadAgentExecutable("not xml"));
    }

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
    public async Task Restore_plan_counts_changes_without_writing_files()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var destination = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(destination, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(source, "different.txt"), "incoming");
        await File.WriteAllTextAsync(Path.Combine(destination, "different.txt"), "existing");
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new");

        try
        {
            var result = await SafeMergeService.PlanDirectoryAsync(source, destination);

            Assert.Equal(new MergeResult(1, 1, 1, 0), result);
            Assert.False(File.Exists(Path.Combine(destination, "new.txt")));
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "different.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Restore_transaction_survives_restart_and_rolls_back_created_files()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var destination = Path.Combine(testRoot, "destination");
        var journals = Path.Combine(testRoot, "journals");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(source, "different.txt"), "incoming");
        await File.WriteAllTextAsync(Path.Combine(destination, "different.txt"), "existing");

        try
        {
            var store = new RestoreTransactionStore(journals);
            var transaction = await store.BeginAsync("snapshot-1", destination);
            var result = await SafeMergeService.MergeDirectoryAsync(
                source, destination, transaction.ConflictRoot, transaction);
            await store.CompleteAsync(transaction, result);

            var reloaded = Assert.Single(await new RestoreTransactionStore(journals).ListAsync());
            Assert.Equal(RestoreTransactionStatus.Completed, reloaded.Status);
            Assert.Equal(2, reloaded.RecordedFiles);

            var rollback = await new RestoreTransactionStore(journals).RollbackAsync(reloaded.Id);

            Assert.True(rollback.Succeeded);
            Assert.Empty(Directory.EnumerateFiles(destination, "*.partial", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(destination, "new.txt")));
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "different.txt")));
            Assert.False(File.Exists(Path.Combine(transaction.ConflictRoot, "different.txt")));
            Assert.Equal(RestoreTransactionStatus.RolledBack,
                Assert.Single(await new RestoreTransactionStore(journals).ListAsync()).Status);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Restore_rollback_preserves_file_changed_after_restore()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var destination = Path.Combine(testRoot, "destination");
        var journals = Path.Combine(testRoot, "journals");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "restored");

        try
        {
            var store = new RestoreTransactionStore(journals);
            var transaction = await store.BeginAsync("snapshot-2", destination);
            var result = await SafeMergeService.MergeDirectoryAsync(
                source, destination, transaction.ConflictRoot, transaction);
            await store.CompleteAsync(transaction, result);
            await File.WriteAllTextAsync(Path.Combine(destination, "new.txt"), "edited by user");

            var rollback = await store.RollbackAsync(transaction.Id);

            Assert.False(rollback.Succeeded);
            Assert.Equal("edited by user", await File.ReadAllTextAsync(Path.Combine(destination, "new.txt")));
            Assert.Equal(RestoreTransactionStatus.RollbackBlocked,
                Assert.Single(await store.ListAsync()).Status);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Restore_transaction_reports_interrupted_work_after_restart()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source.txt");
        var destinationRoot = Path.Combine(testRoot, "destination");
        var destination = Path.Combine(destinationRoot, "source.txt");
        var journals = Path.Combine(testRoot, "journals");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(source, "restored");

        try
        {
            var store = new RestoreTransactionStore(journals);
            var transaction = await store.BeginAsync("snapshot-3", destinationRoot);
            await SafeMergeService.MergeFileAsync(
                source, destination, Path.Combine(transaction.ConflictRoot, "source.txt"), transaction);
            var partial = $"{destination}.codexbridge-{transaction.Id}.partial";
            File.Move(destination, partial);

            var restartedStore = new RestoreTransactionStore(journals);
            var reloaded = Assert.Single(await restartedStore.ListAsync());

            Assert.True(reloaded.NeedsAttention);
            Assert.True(reloaded.CanRollback);
            Assert.Equal(1, reloaded.RecordedFiles);
            Assert.True((await restartedStore.RollbackAsync(reloaded.Id)).Succeeded);
            Assert.False(File.Exists(partial));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task ProcessRunner_cancellation_stops_a_long_running_process()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(executable, ["127.0.0.1", "-n", "30"], cancellationToken: cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"Отмена заняла {stopwatch.Elapsed}.");
    }

    [Fact]
    public void ValidateRestoredSnapshot_detects_missing_projects()
    {
        var staging = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var original = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Projects", "Alpha");
        var drive = Path.GetPathRoot(original)!.TrimEnd('\\', '/').TrimEnd(':');
        var relative = original[Path.GetPathRoot(original)!.Length..];
        var restored = Path.Combine(staging, drive, relative);
        var manifest = new BackupManifest
        {
            Projects = [new ManifestProject { Name = "Alpha", SourcePath = original }]
        };

        try
        {
            Directory.CreateDirectory(restored);
            Assert.True(RestoreService.ValidateRestoredSnapshot(staging, manifest).Succeeded);

            Directory.Delete(restored, true);
            var missing = RestoreService.ValidateRestoredSnapshot(staging, manifest);
            Assert.False(missing.Succeeded);
            Assert.Contains("отсутствует проектов 1", missing.Message);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    [Fact]
    public void ResolveDestinationToken_blocks_escape_from_known_folders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var safe = RestoreService.ResolveDestinationToken(@"{UserProfile}\CodexBridge-Recovery\apps.json");

        Assert.Equal(Path.Combine(profile, "CodexBridge-Recovery", "apps.json"), safe, ignoreCase: true);
        Assert.Throws<InvalidDataException>(() =>
            RestoreService.ResolveDestinationToken(@"{UserProfile}\..\outside.txt"));
        Assert.Throws<InvalidDataException>(() =>
            RestoreService.ResolveDestinationToken(@"C:\Windows\System32\drivers\etc\hosts"));
    }

    [Fact]
    public void IsInside_rejects_similar_prefix()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexBridge-data"));
        var sibling = root + "-old";

        Assert.False(PathPolicy.IsInside(sibling, root));
        Assert.True(PathPolicy.IsInside(Path.Combine(root, "project"), root));
    }

    [Fact]
    public void ParseExtensions_removes_comments_blanks_and_duplicates()
    {
        var result = ToolInventoryService.ParseExtensions("publisher.one@1.0\r\n# note\r\n\r\nPublisher.One@1.0\r\npublisher.two");

        Assert.Equal(["publisher.one@1.0", "publisher.two"], result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverEnvironment_excludes_vscode_when_disabled()
    {
        var result = BackupFiles.DiscoverEnvironment(includeVsCode: false);

        Assert.DoesNotContain(result, item => item.Name.StartsWith("VS Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ErrorLog_writes_utf8_file_only_when_called()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));

        try
        {
            Assert.False(Directory.Exists(directory));
            var path = ErrorLog.Write("Тест", "Ошибка снимка", "Техническая причина", directory);

            Assert.Equal(Path.Combine(directory, ErrorLog.FileName), path);
            var contents = File.ReadAllText(path);
            Assert.Contains("Ошибка снимка", contents);
            Assert.Contains("Техническая причина", contents);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Migration_lab_restores_real_snapshot_idempotently()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEXBRIDGE_RUN_RESTIC_INTEGRATION"), "1",
                StringComparison.Ordinal))
            return;

        var executable = Environment.GetEnvironmentVariable("CODEXBRIDGE_RESTIC_EXECUTABLE") ?? "restic";
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var machineA = Path.Combine(testRoot, "machine-a");
        var machineB = Path.Combine(testRoot, "machine-b");
        var projectAlpha = Path.Combine(machineA, "Projects", "Project Alpha");
        var projectSecondary = Path.Combine(machineA, "Projects", "Project Secondary");
        var config = Path.Combine(machineA, "Profile", ".codex", "config.toml");
        var manifestPath = Path.Combine(machineA, "CodexBridge", "codexbridge-backup-manifest.json");
        var repository = Path.Combine(testRoot, "repository");
        var excludes = Path.Combine(testRoot, "restic-excludes.txt");
        var cache = Path.Combine(testRoot, "restic-cache");
        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        try
        {
            Directory.CreateDirectory(Path.Combine(projectAlpha, "src"));
            Directory.CreateDirectory(Path.Combine(projectSecondary, "docs"));
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(Path.Combine(projectAlpha, "src", "app.txt"), "codexbridge-v1");
            await File.WriteAllTextAsync(Path.Combine(projectSecondary, "docs", "guide.md"), "secondary project");
            await File.WriteAllTextAsync(config, "model = 'test-only'");
            await File.WriteAllTextAsync(excludes, "**/.env\n**/auth.json\n");

            var manifest = new BackupManifest
            {
                ApplicationVersion = "migration-lab",
                MachineName = "MACHINE-A",
                CreatedUtc = DateTimeOffset.UtcNow,
                Projects =
                [
                    new ManifestProject { Id = Guid.NewGuid(), Name = "Project Alpha", SourcePath = projectAlpha },
                    new ManifestProject { Id = Guid.NewGuid(), Name = "Project Secondary", SourcePath = projectSecondary }
                ],
                Environment =
                [
                    new ManifestEnvironmentItem
                    {
                        Name = "Codex config",
                        SourcePath = config,
                        DestinationToken = @"{UserProfile}\.codex\config.toml"
                    }
                ]
            };
            var files = new JsonFileStore();
            await files.SaveAsync(manifestPath, manifest);

            var processes = new ProcessRunner();
            var restic = new ResticService(processes, cache, excludes);
            Assert.True((await restic.InitializeAsync(executable, repository, password)).Succeeded);
            var sources = new[] { projectAlpha, projectSecondary, config, manifestPath };
            Assert.True((await restic.BackupAsync(executable, repository, password, sources)).Succeeded);

            await File.WriteAllTextAsync(Path.Combine(projectAlpha, "src", "app.txt"), "codexbridge-v2");
            await File.WriteAllTextAsync(Path.Combine(projectAlpha, "README.md"), "second snapshot");
            manifest.CreatedUtc = DateTimeOffset.UtcNow;
            await files.SaveAsync(manifestPath, manifest);
            Assert.True((await restic.BackupAsync(executable, repository, password, sources)).Succeeded);

            var environment = new Dictionary<string, string>
            {
                ["RESTIC_PASSWORD"] = password,
                ["RESTIC_CACHE_DIR"] = cache
            };
            Assert.True((await processes.RunAsync(executable,
                ["-r", repository, "check", "--read-data-subset=100%"], environment)).Succeeded);
            Assert.True((await processes.RunAsync(executable,
                ["-r", repository, "forget", "--tag", "codexbridge", "--keep-last", "1", "--prune"],
                environment)).Succeeded);
            var snapshot = Assert.Single(await restic.SnapshotsAsync(executable, repository, password));

            var stagingA = Path.Combine(testRoot, "staging-a");
            var restore = await restic.RestoreRawAsync(
                executable, repository, password, snapshot.Id, stagingA, verify: true);
            Assert.True(restore.Succeeded, $"{restore.Message}{Environment.NewLine}{restore.Details}");
            var restoredManifestPath = Assert.Single(Directory.EnumerateFiles(
                stagingA, Path.GetFileName(manifestPath), SearchOption.AllDirectories));
            var restoredManifest = await files.LoadAsync(restoredManifestPath, () => new BackupManifest());
            Assert.True(RestoreService.ValidateRestoredSnapshot(stagingA, restoredManifest).Succeeded);

            var journals = Path.Combine(testRoot, "journals");
            var firstStore = new RestoreTransactionStore(journals);
            var firstTransaction = await firstStore.BeginAsync(snapshot.Id, machineB);
            var first = await MergeMigrationAsync(stagingA, machineB, restoredManifest, firstTransaction);
            await firstStore.CompleteAsync(firstTransaction, first);
            Assert.Equal(new MergeResult(4, 0, 0, 0), first);

            var changedDestination = Path.Combine(machineB, "Projects", "Project Alpha", "src", "app.txt");
            await File.WriteAllTextAsync(changedDestination, "machine-b-local-change");

            var stagingB = Path.Combine(testRoot, "staging-b");
            Assert.True((await restic.RestoreRawAsync(
                executable, repository, password, snapshot.Id, stagingB, verify: true)).Succeeded);
            var secondStore = new RestoreTransactionStore(journals);
            var secondTransaction = await secondStore.BeginAsync(snapshot.Id, machineB);
            var second = await MergeMigrationAsync(stagingB, machineB, restoredManifest, secondTransaction);
            await secondStore.CompleteAsync(secondTransaction, second);

            Assert.Equal(new MergeResult(0, 3, 1, 0), second);
            Assert.Equal("machine-b-local-change", await File.ReadAllTextAsync(changedDestination));
            Assert.Equal("codexbridge-v2", await File.ReadAllTextAsync(Path.Combine(
                secondTransaction.ConflictRoot, "Project Alpha", "src", "app.txt")));
            Console.WriteLine($"MIGRATION_LAB_OK=snapshot={snapshot.Id};added={first.Added};identical={second.Identical};conflicts={second.Conflicts}");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    private static async Task<MergeResult> MergeMigrationAsync(
        string staging,
        string destinationRoot,
        BackupManifest manifest,
        RestoreTransaction transaction)
    {
        var total = new MergeResult(0, 0, 0, 0);
        foreach (var project in manifest.Projects)
        {
            var source = Assert.Single(Directory.EnumerateDirectories(
                staging, project.Name, SearchOption.AllDirectories));
            var result = await SafeMergeService.MergeDirectoryAsync(
                source,
                Path.Combine(destinationRoot, "Projects", project.Name),
                Path.Combine(transaction.ConflictRoot, project.Name),
                transaction);
            total = Combine(total, result);
        }

        var config = Assert.Single(Directory.EnumerateFiles(staging, "config.toml", SearchOption.AllDirectories));
        var configResult = await SafeMergeService.MergeFileAsync(
            config,
            Path.Combine(destinationRoot, "Profile", ".codex", "config.toml"),
            Path.Combine(transaction.ConflictRoot, "environment", "config.toml"),
            transaction);
        return Combine(total, configResult);
    }

    private static MergeResult Combine(MergeResult left, MergeResult right) => new(
        left.Added + right.Added,
        left.Identical + right.Identical,
        left.Conflicts + right.Conflicts,
        left.Skipped + right.Skipped);
}
