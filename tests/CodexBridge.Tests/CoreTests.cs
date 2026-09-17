using CodexBridge.Core;
using System.IO.Compression;
using System.Text.Json;

namespace CodexBridge.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ProjectCatalogFilter_matches_name_path_and_status()
    {
        var project = new ProjectEntry
        {
            Name = "Демо-проект",
            Path = @"C:\Projects\demo-service",
            IsProtected = true,
            Status = ProjectStatus.Protected
        };

        Assert.True(ProjectCatalogFilter.Matches(project, "демо", ProjectListFilter.All));
        Assert.True(ProjectCatalogFilter.Matches(project, "DEMO-SERVICE", ProjectListFilter.Protected));
        Assert.False(ProjectCatalogFilter.Matches(project, "ДЕМО-ПРОЕКТ", ProjectListFilter.Excluded));
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
    public void Uninstall_owns_only_a_single_action_pointing_to_its_agent()
    {
        var executable = Path.GetTempFileName();
        try
        {
            string Xml(string command, string extra = "") => new System.Xml.Linq.XElement("Task",
                new System.Xml.Linq.XElement("Actions",
                    new System.Xml.Linq.XElement("Exec", new System.Xml.Linq.XElement("Command", command)),
                    string.IsNullOrEmpty(extra) ? null : System.Xml.Linq.XElement.Parse(extra))).ToString();
            Assert.True(SchedulerService.TaskBelongsToAgent(Xml('"' + executable.ToUpperInvariant() + '"'), executable));
            Assert.False(SchedulerService.TaskBelongsToAgent(Xml(executable + ".other"), executable));
            Assert.False(SchedulerService.TaskBelongsToAgent(Xml(executable, "<Exec><Command>other.exe</Command></Exec>"), executable));
            Assert.False(SchedulerService.TaskBelongsToAgent("invalid XML", executable));
        }
        finally { File.Delete(executable); }
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
    public async Task SafeMerge_locked_destination_fails_without_overwriting_or_partial_files()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source.txt");
        var destination = Path.Combine(testRoot, "destination.txt");
        var conflict = Path.Combine(testRoot, "conflicts", "destination.txt");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(source, "incoming");
        await File.WriteAllTextAsync(destination, "existing");

        try
        {
            await using var locked = new FileStream(
                destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                SafeMergeService.MergeFileAsync(source, destination, conflict));

            Assert.False(File.Exists(conflict));
            Assert.Empty(Directory.EnumerateFiles(testRoot, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Restore_transaction_rolls_back_after_destination_directory_becomes_unavailable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source.txt");
        var destinationRoot = Path.Combine(testRoot, "destination");
        var blockedDirectory = Path.Combine(destinationRoot, "blocked");
        var destination = Path.Combine(blockedDirectory, "source.txt");
        var journals = Path.Combine(testRoot, "journals");
        Directory.CreateDirectory(destinationRoot);
        await File.WriteAllTextAsync(source, "restored");
        await File.WriteAllTextAsync(blockedDirectory, "this file blocks directory creation");

        try
        {
            var store = new RestoreTransactionStore(journals);
            var transaction = await store.BeginAsync("snapshot-unavailable", destinationRoot);

            await Assert.ThrowsAnyAsync<IOException>(() => SafeMergeService.MergeFileAsync(
                source, destination, Path.Combine(transaction.ConflictRoot, "source.txt"), transaction));

            var restarted = new RestoreTransactionStore(journals);
            var reloaded = Assert.Single(await restarted.ListAsync());
            Assert.True(reloaded.NeedsAttention);
            Assert.True((await restarted.RollbackAsync(reloaded.Id)).Succeeded);
            Assert.Equal("this file blocks directory creation", await File.ReadAllTextAsync(blockedDirectory));
            Assert.Empty(Directory.EnumerateFiles(testRoot, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Restore_rollback_ignores_only_a_torn_final_journal_record()
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
            var transaction = await store.BeginAsync("snapshot-torn-journal", destinationRoot);
            var result = await SafeMergeService.MergeFileAsync(
                source, destination, Path.Combine(transaction.ConflictRoot, "source.txt"), transaction);
            await store.CompleteAsync(transaction, result);
            await File.AppendAllTextAsync(
                Path.Combine(store.GetTransactionDirectory(transaction.Id), "files.jsonl"),
                Environment.NewLine + "{\"sequence\":");

            var rollback = await new RestoreTransactionStore(journals).RollbackAsync(transaction.Id);

            Assert.True(rollback.Succeeded);
            Assert.False(File.Exists(destination));
            Assert.Equal(RestoreTransactionStatus.RolledBack,
                Assert.Single(await store.ListAsync()).Status);
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
        Assert.Equal("publisher.one", ToolInventoryService.GetExtensionId("publisher.one@1.0"));
        Assert.Equal("publisher.two", ToolInventoryService.GetExtensionId("publisher.two"));
    }

    [Fact]
    public void Winget_plan_removes_already_installed_packages()
    {
        const string inventory = """
                                 {
                                   "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
                                   "Sources": [
                                     {
                                       "Packages": [
                                         { "PackageIdentifier": "Git.Git", "Version": "2.50.0" },
                                         { "PackageIdentifier": "OpenJS.NodeJS.LTS", "Version": "22.0.0" }
                                       ],
                                       "SourceDetails": { "Name": "winget", "Identifier": "test" }
                                     }
                                   ]
                                 }
                                 """;

        var plan = ToolInventoryService.BuildWingetInventoryPlan(inventory, ["git.git"]);
        var filtered = ToolInventoryService.BuildWingetInventoryPlan(plan.PendingInventoryJson, []);

        Assert.Equal(["Git.Git", "OpenJS.NodeJS.LTS"], plan.RequestedPackageIds);
        Assert.Equal(["Git.Git"], plan.InstalledPackageIds);
        Assert.Equal(["OpenJS.NodeJS.LTS"], plan.PendingPackageIds);
        Assert.Equal(["OpenJS.NodeJS.LTS"], filtered.RequestedPackageIds);
    }

    [Fact]
    public void Portable_path_tokens_cannot_escape_allowed_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Tools", "bin");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(outside);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{UserProfile}"] = root
        };
        try
        {
            var token = ToolInventoryService.TokenizePortablePath(bin, roots);

            Assert.Equal(@"{UserProfile}\Tools\bin", token);
            Assert.Equal(bin, ToolInventoryService.ResolvePortablePathToken(token, roots), ignoreCase: true);
            Assert.Null(ToolInventoryService.ResolvePortablePathToken(
                @"{UserProfile}\..\" + Path.GetFileName(outside), roots));
            Assert.Null(ToolInventoryService.ResolvePortablePathToken(@"{Unknown}\Tools", roots));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Portable_environment_plan_is_empty_on_second_run_and_preserves_conflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Tools", "bin");
        var jdk = Path.Combine(root, "Java", "jdk");
        var otherJdk = Path.Combine(root, "Java", "other");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(jdk);
        Directory.CreateDirectory(otherJdk);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{UserProfile}"] = root
        };
        var profile = new PortableEnvironmentProfile
        {
            UserPathEntries = [@"{UserProfile}\Tools\bin"],
            UserVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["JAVA_HOME"] = @"{UserProfile}\Java\jdk",
                ["API_TOKEN"] = @"{UserProfile}\Java\jdk"
            }
        };
        try
        {
            var firstRun = ToolInventoryService.BuildPortableEnvironmentPlan(
                profile, "", new Dictionary<string, string?>(), roots);
            var secondRun = ToolInventoryService.BuildPortableEnvironmentPlan(
                profile, bin, new Dictionary<string, string?> { ["JAVA_HOME"] = jdk }, roots);
            var conflict = ToolInventoryService.BuildPortableEnvironmentPlan(
                profile, bin, new Dictionary<string, string?> { ["JAVA_HOME"] = otherJdk }, roots);

            Assert.Equal([bin], firstRun.PathEntriesToAdd, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(jdk, firstRun.VariablesToAdd["JAVA_HOME"], ignoreCase: true);
            Assert.Empty(secondRun.PathEntriesToAdd);
            Assert.Equal(1, secondRun.ExistingPathEntries);
            Assert.Empty(secondRun.VariablesToAdd);
            Assert.Empty(secondRun.VariableConflicts);
            Assert.Contains("переменная: API_TOKEN", secondRun.RejectedOrMissingEntries);
            Assert.Equal(["JAVA_HOME"], conflict.VariableConflicts);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DiscoverEnvironment_excludes_vscode_when_disabled()
    {
        var result = BackupFiles.DiscoverEnvironment(includeVsCode: false);

        Assert.DoesNotContain(result, item => item.Name.StartsWith("VS Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SanitizeCodexConfig_removes_mcp_secrets_and_preserves_safe_settings()
    {
        const string source = """
                              model = "gpt-test"
                              [mcp_servers.demo]
                              command = "node"
                              env = { API_TOKEN = "inline-secret" }
                              [mcp_servers.demo.env]
                              API_KEY = "section-secret"
                              MODE = "development"
                              [features]
                              theme = "dark"
                              password = "top-secret"
                              """;

        var sanitized = ToolInventoryService.SanitizeCodexConfig(source, out var redacted);

        Assert.Equal(4, redacted);
        Assert.Contains("model = \"gpt-test\"", sanitized);
        Assert.Contains("command = \"node\"", sanitized);
        Assert.Contains("theme = \"dark\"", sanitized);
        Assert.DoesNotContain("inline-secret", sanitized);
        Assert.DoesNotContain("section-secret", sanitized);
        Assert.DoesNotContain("development", sanitized);
        Assert.DoesNotContain("top-secret", sanitized);
    }

    [Fact]
    public void PortableGitProfile_allows_only_non_secret_portable_keys()
    {
        Assert.True(ToolInventoryService.IsPortableGitKey("user.email"));
        Assert.True(ToolInventoryService.IsPortableGitKey("core.autocrlf"));
        Assert.False(ToolInventoryService.IsPortableGitKey("credential.helper"));
        Assert.False(ToolInventoryService.IsPortableGitKey("http.extraHeader"));
        Assert.False(ToolInventoryService.IsPortableGitKey("url.https://token@example.test.insteadOf"));
    }

    [Fact]
    public void ParseMcpServerNames_deduplicates_nested_sections()
    {
        const string config = """
                              [mcp_servers.graphify]
                              command = "graphify"
                              [mcp_servers.graphify.env]
                              MODE = "safe"
                              [mcp_servers.codebase-memory-mcp]
                              command = "server"
                              """;

        var names = EnvironmentDiagnosticsService.ParseMcpServerNames(config);

        Assert.Equal(["codebase-memory-mcp", "graphify"], names);
    }

    [Fact]
    public async Task AnalyzeObsidianRegistry_reports_existing_vaults_inside_project_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "Projects");
        var existingVault = Path.Combine(projectRoot, "Notes");
        var missingVault = Path.Combine(root, "Old", "Missing");
        var registry = Path.Combine(root, "obsidian.json");
        Directory.CreateDirectory(existingVault);
        try
        {
            await File.WriteAllTextAsync(registry, JsonSerializer.Serialize(new
            {
                vaults = new Dictionary<string, object>
                {
                    ["one"] = new { path = existingVault },
                    ["two"] = new { path = missingVault }
                }
            }));

            var result = await EnvironmentDiagnosticsService.AnalyzeObsidianRegistryAsync(
                registry, [projectRoot]);

            Assert.Equal(new ObsidianVaultSummary(2, 1, 1), result);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessRunner_runs_command_wrappers_without_interpreting_arguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"), "command with spaces");
        Directory.CreateDirectory(root);
        var command = Path.Combine(root, "echo-argument.cmd");
        await File.WriteAllTextAsync(command, "@echo off\r\necho \"%~1\"\r\n");
        try
        {
            var result = await new ProcessRunner().RunCommandAsync(command, ["alpha & beta"]);

            Assert.True(result.Succeeded, result.Combined);
            Assert.Contains("alpha & beta", result.Output);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public void GetIndexableProjects_keeps_only_unique_protected_existing_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projects = new[]
            {
                new ProjectEntry { Name = "One", Path = root, IsProtected = true },
                new ProjectEntry { Name = "Duplicate", Path = root, IsProtected = true },
                new ProjectEntry { Name = "Excluded", Path = root, IsProtected = false },
                new ProjectEntry { Name = "Missing", Path = Path.Combine(root, "missing"), IsProtected = true }
            };

            var result = EnvironmentAutomationService.GetIndexableProjects(projects);

            Assert.Single(result);
            Assert.Equal("One", result[0].Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RebindObsidianRegistry_maps_unique_vault_and_preserves_existing_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "Projects");
        var vault = Path.Combine(destination, "VaultA");
        var source = Path.Combine(root, "recovery", "obsidian-vaults.json");
        var target = Path.Combine(root, "appdata", "obsidian.json");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["restored"] = new { path = @"C:\OldComputer\VaultA", ts = 1 }
            }
        }));
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["current"] = new { path = destination, ts = 2 }
            }
        }));
        try
        {
            var result = await EnvironmentAutomationService.RebindObsidianRegistryAsync(
                source, target, destination, [vault]);

            Assert.True(result.Succeeded, result.Details);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(target));
            var vaults = document.RootElement.GetProperty("vaults");
            Assert.Equal(vault, vaults.GetProperty("restored").GetProperty("path").GetString());
            Assert.Equal(destination, vaults.GetProperty("current").GetProperty("path").GetString());
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!, "obsidian.codexbridge-backup-*.json"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RebindObsidianRegistry_leaves_ambiguous_vault_untouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "Projects");
        var first = Path.Combine(destination, "One", "VaultA");
        var second = Path.Combine(destination, "Two", "VaultA");
        var source = Path.Combine(root, "recovery.json");
        var target = Path.Combine(root, "obsidian.json");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["restored"] = new { path = @"C:\OldComputer\VaultA" }
            }
        }));
        try
        {
            var result = await EnvironmentAutomationService.RebindObsidianRegistryAsync(
                source, target, destination, [first, second]);

            Assert.False(result.Succeeded);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
    public async Task DiagnosticsBundle_excludes_settings_and_redacts_log_secrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));
        var log = Path.Combine(root, "errors.log");
        var output = Path.Combine(root, "diagnostics.zip");
        var projectRoot = Path.Combine(root, "Private Project");
        var token = "gh" + "p_" + new string('a', 24);
        var privateKeyHeader = "-----BEGIN " + "PRIVATE KEY-----";
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(log,
            $"Private Project failed at {projectRoot} user=user@example.com token={token} "
            + "Bearer abc.def.ghi https://example.test/path?access_token=unsafe\n"
            + $"{privateKeyHeader}\nprivate-material\n-----END PRIVATE KEY-----");
        var settings = new AppSettings
        {
            SetupCompleted = true,
            ProjectRoots = [projectRoot],
            LocalRepository = Path.Combine(root, "repository"),
            CloudRepository = "rclone:private-account:SecretFolder",
            CloudEnabled = true,
            DestinationRoot = projectRoot
        };

        try
        {
            var service = new DiagnosticsBundleService([log]);
            var path = await service.CreateAsync(
                output, settings, new BackupState { LastRunSucceeded = false }, null, "test-version",
                projects: [new ProjectEntry { Name = "Private Project", Path = projectRoot }]);

            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "summary.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "errors-1.log");
            var text = string.Join("\n", archive.Entries.Select(entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }));
            Assert.Contains("test-version", text);
            Assert.Contains("[REDACTED]", text);
            Assert.DoesNotContain(projectRoot, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Private Project", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-material", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-account", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretFolder", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("user@example.com", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UpdateService_selects_newest_complete_release_and_verifies_installer_hash()
    {
        var installerBytes = "synthetic verified installer"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToLowerInvariant();
        const string version = "0.10.0-build.31.1";
        var installerName = $"CodexBridge-{version}-setup.exe";
        var api = JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = "v0.11.0-build.1.1", html_url = "https://github.com/lebrit/CodexBridge/releases/tag/incomplete",
                draft = false, assets = Array.Empty<object>()
            },
            new
            {
                tag_name = $"v{version}", html_url = $"https://github.com/lebrit/CodexBridge/releases/tag/v{version}",
                draft = false,
                assets = new object[]
                {
                    new { name = installerName, browser_download_url = $"https://github.com/lebrit/CodexBridge/releases/download/v{version}/{installerName}" },
                    new { name = installerName + ".sha256", browser_download_url = $"https://github.com/lebrit/CodexBridge/releases/download/v{version}/{installerName}.sha256" }
                }
            }
        });
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(api) };
            if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash}  {installerName}")
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installerBytes)
            };
        }));
        var service = new UpdateService(client);
        var root = Path.Combine(Path.GetTempPath(), "CodexBridge-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var update = await service.GetLatestAsync("0.9.1-build.28.1");
            Assert.NotNull(update);
            Assert.Equal(version, update.Version);
            var path = await service.DownloadVerifiedInstallerAsync(update, root);
            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("0.9.1-build.29.1", "0.9.1-build.28.1", true)]
    [InlineData("0.9.1", "0.9.1-build.99.1", true)]
    [InlineData("0.9.1-build.99.1", "0.9.1", false)]
    [InlineData("0.9.1-build.28.1", "0.9.1-build.28.1", false)]
    [InlineData("invalid", "0.9.1", false)]
    public void UpdateService_compares_release_versions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewer(candidate, current));

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
        var gitProfile = Path.Combine(machineA, "Profile", "CodexBridge-Recovery", "git-profile.json");
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
            Directory.CreateDirectory(Path.GetDirectoryName(gitProfile)!);
            await File.WriteAllTextAsync(gitProfile, "{\"settings\":{\"core.autocrlf\":\"true\"}}");
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
                    },
                    new ManifestEnvironmentItem
                    {
                        Name = "Portable Git profile",
                        SourcePath = gitProfile,
                        DestinationToken = @"{UserProfile}\CodexBridge-Recovery\git-profile.json"
                    }
                ],
                ExcludedForSafety = BackupFiles.ExcludedForSafety.ToList(),
                RequiresManualAction = BackupFiles.RequiresManualAction.ToList()
            };
            var files = new JsonFileStore();
            await files.SaveAsync(manifestPath, manifest);
            File.SetAttributes(projectAlpha, File.GetAttributes(projectAlpha) | FileAttributes.ReadOnly);

            var processes = new ProcessRunner();
            var restic = new ResticService(processes, cache, excludes);
            Assert.True((await restic.InitializeAsync(executable, repository, password)).Succeeded);
            var sources = new[] { projectAlpha, projectSecondary, config, gitProfile, manifestPath };
            Assert.True((await restic.BackupAsync(executable, repository, password, sources)).Succeeded);

            await File.WriteAllTextAsync(Path.Combine(projectAlpha, "src", "app.txt"), "codexbridge-v2");
            await File.WriteAllTextAsync(Path.Combine(projectAlpha, "README.md"), "second snapshot");
            manifest.CreatedUtc = DateTimeOffset.UtcNow;
            await files.SaveAsync(manifestPath, manifest);
            Assert.True((await restic.BackupAsync(executable, repository, password, sources)).Succeeded);
            File.SetAttributes(projectAlpha, File.GetAttributes(projectAlpha) & ~FileAttributes.ReadOnly);

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
            var restoredProjectAlpha = Assert.Single(Directory.EnumerateDirectories(
                stagingA, "Project Alpha", SearchOption.AllDirectories));
            Assert.False(File.GetAttributes(restoredProjectAlpha).HasFlag(FileAttributes.ReadOnly));
            var restoredManifestPath = Assert.Single(Directory.EnumerateFiles(
                stagingA, Path.GetFileName(manifestPath), SearchOption.AllDirectories));
            var restoredManifest = await files.LoadAsync(restoredManifestPath, () => new BackupManifest());
            Assert.True(RestoreService.ValidateRestoredSnapshot(stagingA, restoredManifest).Succeeded);
            Assert.NotEmpty(restoredManifest.ExcludedForSafety);
            Assert.NotEmpty(restoredManifest.RequiresManualAction);

            var journals = Path.Combine(testRoot, "journals");
            var firstStore = new RestoreTransactionStore(journals);
            var firstTransaction = await firstStore.BeginAsync(snapshot.Id, machineB);
            var first = await MergeMigrationAsync(stagingA, machineB, restoredManifest, firstTransaction);
            await firstStore.CompleteAsync(firstTransaction, first);
            Assert.Equal(new MergeResult(5, 0, 0, 0), first);

            var changedDestination = Path.Combine(machineB, "Projects", "Project Alpha", "src", "app.txt");
            await File.WriteAllTextAsync(changedDestination, "machine-b-local-change");

            var stagingB = Path.Combine(testRoot, "staging-b");
            Assert.True((await restic.RestoreRawAsync(
                executable, repository, password, snapshot.Id, stagingB, verify: true)).Succeeded);
            var secondStore = new RestoreTransactionStore(journals);
            var secondTransaction = await secondStore.BeginAsync(snapshot.Id, machineB);
            var second = await MergeMigrationAsync(stagingB, machineB, restoredManifest, secondTransaction);
            await secondStore.CompleteAsync(secondTransaction, second);

            Assert.Equal(new MergeResult(0, 4, 1, 0), second);
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

        foreach (var item in manifest.Environment)
        {
            var source = File.Exists(item.SourcePath)
                ? Assert.Single(Directory.EnumerateFiles(
                    staging, Path.GetFileName(item.SourcePath), SearchOption.AllDirectories))
                : Assert.Single(Directory.EnumerateDirectories(
                    staging, Path.GetFileName(item.SourcePath), SearchOption.AllDirectories));
            var relative = item.DestinationToken
                .Replace("{UserProfile}", "Profile", StringComparison.OrdinalIgnoreCase)
                .Replace("{AppData}", "AppData", StringComparison.OrdinalIgnoreCase)
                .TrimStart('\\', '/');
            var destination = Path.Combine(destinationRoot, relative);
            var conflicts = Path.Combine(transaction.ConflictRoot, "environment", item.Name);
            var result = Directory.Exists(source)
                ? await SafeMergeService.MergeDirectoryAsync(source, destination, conflicts, transaction)
                : await SafeMergeService.MergeFileAsync(
                    source, destination, Path.Combine(conflicts, Path.GetFileName(destination)), transaction);
            total = Combine(total, result);
        }

        return total;
    }

    private static MergeResult Combine(MergeResult left, MergeResult right) => new(
        left.Added + right.Added,
        left.Identical + right.Identical,
        left.Conflicts + right.Conflicts,
        left.Skipped + right.Skipped);
}

file sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(responder(request));
}
