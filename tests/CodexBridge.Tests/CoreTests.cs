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
}
