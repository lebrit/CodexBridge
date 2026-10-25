using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBridge.Core;

public sealed class DiagnosticsBundleService
{
    private const int MaximumLogBytes = 256 * 1024;
    private static readonly Regex UserProfilePath = new(
        @"(?i)(?<drive>[a-z]:)\\users\\[^\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignment = new(
        @"(?im)\b(password|passwd|secret|token|authorization|api[_-]?key|access[_-]?key)\b\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerToken = new(
        @"(?i)\bbearer\s+[a-z0-9._~+/-]+=*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KnownToken = new(
        @"(?i)\b(?:gh[pousr]_[a-z0-9]{16,}|github_pat_[a-z0-9_]{16,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKey = new(
        @"(?is)-----BEGIN [^-\r\n]*PRIVATE KEY-----.*?-----END [^-\r\n]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EmailAddress = new(
        @"(?i)\b[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemoteUrl = new(
        @"(?i)\b(?:https?|s3|sftp)://[^\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IpAddress = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<string>? logPaths;

    public DiagnosticsBundleService(IReadOnlyList<string>? logPaths = null) => this.logPaths = logPaths;

    public async Task<string> CreateAsync(
        string destinationPath,
        AppSettings settings,
        BackupState state,
        EnvironmentDiagnosticReport? environment,
        string applicationVersion,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<ProjectEntry>? projects = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Укажите путь диагностического архива.", nameof(destinationPath));

        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            destination += ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial";

        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var summary = new
                {
                    schemaVersion = 1,
                    createdUtc = DateTimeOffset.UtcNow,
                    applicationVersion = applicationVersion.Trim(),
                    operatingSystem = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    settings = new
                    {
                        setupCompleted = settings.SetupCompleted,
                        theme = settings.Theme,
                        projectRootCount = settings.ProjectRoots.Count,
                        cloudEnabled = settings.CloudEnabled,
                        retentionEnabled = settings.RetentionEnabled,
                        automaticBackupConfigured = !string.IsNullOrWhiteSpace(settings.ScheduledTaskName)
                    },
                    state = new
                    {
                        state.LastLocalBackupUtc,
                        state.LastCloudBackupUtc,
                        state.LastCheckUtc,
                        state.LastRunUtc,
                        state.LastRunSucceeded,
                        state.LastRestoreTestUtc,
                        state.LastRestoreTestSucceeded,
                        recentActivityCount = state.RecentActivities?.Count ?? 0
                    },
                    environment = environment is null ? null : new
                    {
                        environment.ReadyCount,
                        environment.ActionRequiredCount,
                        environment.OptionalCount
                    }
                };
                await WriteEntryAsync(
                    archive, "summary.json",
                    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);

                if (environment is not null)
                    await WriteEntryAsync(
                        archive, "environment.txt",
                        Sanitize(environment.Summary + Environment.NewLine + environment.Details, settings, projects),
                        cancellationToken);

                var availableLogs = (logPaths ?? [ErrorLog.PreferredPath, ErrorLog.FallbackPath])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(File.Exists)
                    .ToArray();
                for (var index = 0; index < availableLogs.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var content = await ReadLogTailAsync(availableLogs[index], cancellationToken);
                    await WriteEntryAsync(
                        archive, $"errors-{index + 1}.log", Sanitize(content, settings, projects), cancellationToken);
                }

                await WriteEntryAsync(
                    archive, "README.txt",
                    "Архив создан CodexBridge. Он содержит только сводные признаки, результат проверки среды "
                    + "и очищенный хвост журнала ошибок. Настройки, ключ восстановления, имена проектов, "
                    + "активные базы Codex и содержимое резервных копий не включаются.",
                    cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A leftover .partial is safer than hiding the original diagnostics error.
            }
            throw;
        }
    }

    public static string Sanitize(
        string value,
        AppSettings? settings = null,
        IReadOnlyCollection<ProjectEntry>? projects = null)
    {
        var sanitized = value ?? string.Empty;
        var replacements = new List<(string Path, string Token)>();
        AddPath(replacements, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        AddPath(replacements, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        AddPath(replacements, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        AddPath(replacements, Path.GetTempPath(), "%TEMP%");

        if (settings is not null)
        {
            for (var index = 0; index < settings.ProjectRoots.Count; index++)
                AddPath(replacements, settings.ProjectRoots[index], $"%PROJECT_ROOT_{index + 1}%");
            AddPath(replacements, settings.LocalRepository, "%LOCAL_REPOSITORY%");
            AddPath(replacements, settings.DestinationRoot, "%DESTINATION_ROOT%");
            if (!string.IsNullOrWhiteSpace(settings.CloudRepository))
                sanitized = sanitized.Replace(
                    settings.CloudRepository, "%CLOUD_REPOSITORY%", StringComparison.OrdinalIgnoreCase);
        }
        if (projects is not null)
        {
            var index = 0;
            foreach (var project in projects)
            {
                index++;
                AddPath(replacements, project.Path, $"%PROJECT_{index}_PATH%");
                if (project.Name.Trim().Length >= 3)
                    sanitized = sanitized.Replace(
                        project.Name.Trim(), $"%PROJECT_{index}_NAME%", StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var replacement in replacements.OrderByDescending(item => item.Path.Length))
            sanitized = sanitized.Replace(replacement.Path, replacement.Token, StringComparison.OrdinalIgnoreCase);

        sanitized = PrivateKey.Replace(sanitized, "[PRIVATE KEY REDACTED]");
        sanitized = SecretAssignment.Replace(sanitized, "$1=[REDACTED]");
        sanitized = BearerToken.Replace(sanitized, "Bearer [REDACTED]");
        sanitized = KnownToken.Replace(sanitized, "[TOKEN REDACTED]");
        sanitized = EmailAddress.Replace(sanitized, "[EMAIL REDACTED]");
        sanitized = RemoteUrl.Replace(sanitized, "[REMOTE URL REDACTED]");
        sanitized = IpAddress.Replace(sanitized, "[IP REDACTED]");
        return UserProfilePath.Replace(sanitized, "${drive}\\Users\\[USER]");
    }

    private static void AddPath(List<(string Path, string Token)> replacements, string? path, string token)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            replacements.Add((Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), token));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Invalid optional paths are omitted from the replacement list.
        }
    }

    private static async Task<string> ReadLogTailAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumLogBytes)
            stream.Seek(-MaximumLogBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 16 * 1024, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
