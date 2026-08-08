using System.Diagnostics;
using System.Text.Json;

namespace CodexBridge.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
    public string Combined => string.Join(Environment.NewLine, new[] { Output, Error }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new ProcessResult(-1, "", $"Не удалось запустить {executable}.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProcessResult(-1, "", exception.Message);
        }
    }
}

public sealed class ResticService(ProcessRunner processes)
{
    public Task<ProcessResult> VersionAsync(string executable, CancellationToken cancellationToken = default) =>
        processes.RunAsync(executable, ["version"], cancellationToken: cancellationToken);

    public async Task<OperationResult> InitializeAsync(
        string executable,
        string repository,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return OperationResult.Fail("Не указан репозиторий.");
        if (string.IsNullOrWhiteSpace(password))
            return OperationResult.Fail("Не указан ключ резервной копии.");

        if (!IsRemote(repository))
        {
            var fullPath = Path.GetFullPath(repository);
            Directory.CreateDirectory(fullPath);
            if (File.Exists(Path.Combine(fullPath, "config")))
            {
                var existing = await RunAsync(executable, repository, password, ["snapshots", "--json"], cancellationToken);
                return existing.Succeeded
                    ? OperationResult.Ok("Репозиторий уже готов.")
                    : OperationResult.Fail("Репозиторий существует, но ключ не подошёл.", existing.Combined);
            }

            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                return OperationResult.Fail("Каталог репозитория не пуст. Выберите пустую папку.");
        }

        var result = await RunAsync(executable, repository, password, ["init"], cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Зашифрованный репозиторий создан.")
            : OperationResult.Fail("Не удалось создать репозиторий.", result.Combined);
    }

    public async Task<OperationResult> BackupAsync(
        string executable,
        string repository,
        string password,
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = sourcePaths.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
            return OperationResult.Fail("Нет доступных путей для резервного копирования.");

        var arguments = new List<string> { "backup" };
        arguments.AddRange(paths);
        arguments.AddRange(["--exclude-file", AppPaths.ExcludesFile, "--tag", "codexbridge", "--json"]);
        var result = await RunAsync(executable, repository, password, arguments, cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Снимок создан.", LastNonEmptyLine(result.Output))
            : OperationResult.Fail("Restic не смог создать снимок.", result.Combined);
    }

    public async Task<IReadOnlyList<SnapshotInfo>> SnapshotsAsync(
        string executable,
        string repository,
        string password,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(executable, repository, password, ["snapshots", "--json"], cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Combined);

        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.EnumerateArray().Select(element => new SnapshotInfo(
            element.GetProperty("short_id").GetString() ?? "",
            element.GetProperty("time").GetDateTimeOffset(),
            element.TryGetProperty("hostname", out var host) ? host.GetString() ?? "" : "",
            ReadStrings(element, "paths"),
            ReadStrings(element, "tags"))).OrderByDescending(snapshot => snapshot.Time).ToList();
    }

    public async Task<OperationResult> CheckAsync(
        string executable,
        string repository,
        string password,
        bool deep = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "check" };
        if (deep)
            arguments.Add("--read-data-subset=5%");
        var result = await RunAsync(executable, repository, password, arguments, cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok(deep ? "Глубокая проверка 5% данных завершена." : "Проверка репозитория завершена.", LastNonEmptyLine(result.Output))
            : OperationResult.Fail("Проверка репозитория завершилась ошибкой.", result.Combined);
    }

    public async Task<OperationResult> ApplyRetentionAsync(
        string executable,
        string repository,
        string password,
        int keepDaily,
        int keepWeekly,
        int keepMonthly,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(executable, repository, password,
        [
            "forget", "--tag", "codexbridge",
            "--keep-daily", Math.Clamp(keepDaily, 1, 365).ToString(),
            "--keep-weekly", Math.Clamp(keepWeekly, 1, 104).ToString(),
            "--keep-monthly", Math.Clamp(keepMonthly, 1, 120).ToString(),
            "--prune"
        ], cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Политика хранения применена.", LastNonEmptyLine(result.Output))
            : OperationResult.Fail("Не удалось применить политику хранения.", result.Combined);
    }

    public async Task<OperationResult> RestoreRawAsync(
        string executable,
        string repository,
        string password,
        string snapshot,
        string target,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        var result = await RunAsync(executable, repository, password, ["restore", snapshot, "--target", target], cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Снимок извлечён во временный каталог.", LastNonEmptyLine(result.Output))
            : OperationResult.Fail("Не удалось извлечь снимок.", result.Combined);
    }

    private Task<ProcessResult> RunAsync(
        string executable,
        string repository,
        string password,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var allArguments = new List<string> { "-r", repository };
        allArguments.AddRange(arguments);
        return processes.RunAsync(executable, allArguments, new Dictionary<string, string>
        {
            ["RESTIC_PASSWORD"] = password,
            ["RESTIC_CACHE_DIR"] = AppPaths.ResticCacheDirectory
        }, cancellationToken);
    }

    private static bool IsRemote(string repository) => repository.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.EnumerateArray().Select(item => item.GetString() ?? "").ToList()
            : [];

    private static string LastNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
}
