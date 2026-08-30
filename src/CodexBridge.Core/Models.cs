using System.Text.Json.Serialization;

namespace CodexBridge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
public enum ProjectStatus
{
    Protected,
    Excluded,
    Missing,
    NeedsAttention
}

public enum BackupRunSource
{
    Manual,
    Automatic
}

public sealed class ProjectEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsGit { get; set; }
    public bool IsProtected { get; set; } = true;
    public ProjectStatus Status { get; set; } = ProjectStatus.Protected;
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string StatusDisplay => Status switch
    {
        ProjectStatus.Protected => "Защищён",
        ProjectStatus.Excluded => "Исключён",
        ProjectStatus.Missing => "Папка не найдена",
        _ => "Требует внимания"
    };
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool SetupCompleted { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool IncludeVsCode { get; set; } = true;
    public List<string> ProjectRoots { get; set; } = [];
    public string LocalRepository { get; set; } = "";
    public string CloudRepository { get; set; } = "";
    public bool CloudEnabled { get; set; }
    public string ResticExecutable { get; set; } = "restic";
    public string DestinationRoot { get; set; } = "";
    public int ScanDepth { get; set; } = 6;
    public bool RetentionEnabled { get; set; }
    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 6;
    public string ScheduledTaskName { get; set; } = "CodexBridge Hourly Backup";
}

public sealed class BackupState
{
    public DateTimeOffset? LastLocalBackupUtc { get; set; }
    public DateTimeOffset? LastCloudBackupUtc { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public BackupRunSource LastRunSource { get; set; }
    public bool LastRunSucceeded { get; set; }
    public string LastMessage { get; set; } = "Настройка ещё не завершена.";
    public List<ActivityEntry> RecentActivities { get; set; } = [];

    public void RecordActivity(bool succeeded, string message, DateTimeOffset? recordedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        RecentActivities ??= [];
        RecentActivities.Insert(0, new ActivityEntry
        {
            RecordedUtc = recordedUtc ?? DateTimeOffset.UtcNow,
            Succeeded = succeeded,
            Message = message.Trim()
        });
        if (RecentActivities.Count > 10)
            RecentActivities.RemoveRange(10, RecentActivities.Count - 10);
    }

    public void RecordRun(bool succeeded, string message, BackupRunSource source, DateTimeOffset? recordedUtc = null)
    {
        var timestamp = recordedUtc ?? DateTimeOffset.UtcNow;
        LastRunUtc = timestamp;
        LastRunSource = source;
        LastRunSucceeded = succeeded;
        LastMessage = message.Trim();
        var prefix = source == BackupRunSource.Automatic ? "Автоматически" : "Вручную";
        RecordActivity(succeeded, $"{prefix}: {LastMessage}", timestamp);
    }
}

public sealed class ActivityEntry
{
    public DateTimeOffset RecordedUtc { get; set; }
    public bool Succeeded { get; set; }
    public string Message { get; set; } = "";
}

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ApplicationVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public List<ManifestProject> Projects { get; set; } = [];
    public List<ManifestEnvironmentItem> Environment { get; set; } = [];
}

public sealed class ManifestProject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public bool IsGit { get; set; }
}

public sealed class ManifestEnvironmentItem
{
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationToken { get; set; } = "";
}

public sealed record OperationResult(bool Succeeded, string Message, string Details = "")
{
    public static OperationResult Ok(string message, string details = "") => new(true, message, details);
    public static OperationResult Fail(string message, string details = "") => new(false, message, details);
}

public sealed record ScheduledTaskStatus(bool Installed, bool UsesCurrentAgent, string ConfiguredAgent = "");

public sealed record SnapshotInfo(
    string Id,
    DateTimeOffset Time,
    string Hostname,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags)
{
    public string Display => $"{Time.ToLocalTime():g}  •  {Id}  •  {Hostname}";
}

public sealed record CatalogRefreshResult(
    IReadOnlyList<ProjectEntry> Projects,
    int ScannedDirectories,
    IReadOnlyList<string> Warnings);

public sealed record MergeResult(int Added, int Identical, int Conflicts, int Skipped);
