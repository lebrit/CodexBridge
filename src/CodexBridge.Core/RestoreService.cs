using System.Security.Cryptography;

namespace CodexBridge.Core;

public sealed class RestoreService(ResticService restic, JsonFileStore files)
{
    public async Task<OperationResult> RestoreSnapshotAsync(
        AppSettings settings,
        string repository,
        string password,
        string snapshot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return OperationResult.Fail("Выберите папку восстановления.");

        var staging = Path.Combine(AppPaths.RestoreDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var extract = await restic.RestoreRawAsync(
                settings.ResticExecutable, repository, password, snapshot, staging,
                verify: false, cancellationToken: cancellationToken);
            if (!extract.Succeeded)
                return extract;

            var prepared = await LoadAndValidateManifestAsync(staging, cancellationToken);
            if (!prepared.Result.Succeeded || prepared.Manifest is null)
                return prepared.Result;

            var manifest = prepared.Manifest;
            Directory.CreateDirectory(destinationRoot);
            var runName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var total = new MergeResult(0, 0, 0, 0);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in manifest.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = LocateRestoredPath(staging, project.SourcePath);
                if (source is null || !Directory.Exists(source))
                {
                    total = total with { Skipped = total.Skipped + 1 };
                    continue;
                }

                var name = SafeDirectoryName(project.Name);
                var uniqueName = name;
                for (var index = 2; !names.Add(uniqueName); index++)
                    uniqueName = $"{name}-{index}";

                var destination = Path.Combine(Path.GetFullPath(destinationRoot), uniqueName);
                var conflicts = Path.Combine(Path.GetFullPath(destinationRoot), ".codexbridge-conflicts", runName, uniqueName);
                total = Add(total, await SafeMergeService.MergeDirectoryAsync(source, destination, conflicts, cancellationToken));
            }

            foreach (var item in manifest.Environment)
            {
                var source = LocateRestoredPath(staging, item.SourcePath);
                if (source is null)
                    continue;

                var destination = ResolveDestinationToken(item.DestinationToken);
                var conflicts = Path.Combine(Path.GetFullPath(destinationRoot), ".codexbridge-conflicts", runName, "environment", SafeDirectoryName(item.Name));
                var result = Directory.Exists(source)
                    ? await SafeMergeService.MergeDirectoryAsync(source, destination, conflicts, cancellationToken)
                    : await SafeMergeService.MergeFileAsync(source, destination, Path.Combine(conflicts, Path.GetFileName(destination)), cancellationToken);
                total = Add(total, result);
            }

            return OperationResult.Ok(
                $"Восстановление завершено: добавлено {total.Added}, совпало {total.Identical}, конфликтов {total.Conflicts}, пропущено {total.Skipped}.",
                Path.Combine(destinationRoot, ".codexbridge-conflicts", runName));
        }
        finally
        {
            TryDeleteStaging(staging);
        }
    }

    public async Task<OperationResult> VerifySnapshotAsync(
        AppSettings settings,
        string repository,
        string password,
        string snapshot,
        CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(AppPaths.RestoreDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var extract = await restic.RestoreRawAsync(
                settings.ResticExecutable, repository, password, snapshot, staging,
                verify: true, cancellationToken: cancellationToken);
            if (!extract.Succeeded)
                return OperationResult.Fail("Проверка восстановления не пройдена: снимок не удалось полностью извлечь и проверить.", extract.Details);

            var prepared = await LoadAndValidateManifestAsync(staging, cancellationToken);
            return prepared.Result.Succeeded
                ? OperationResult.Ok(prepared.Result.Message + " Рабочие папки не изменялись.", extract.Details)
                : prepared.Result;
        }
        finally
        {
            TryDeleteStaging(staging);
        }
    }

    private async Task<(BackupManifest? Manifest, OperationResult Result)> LoadAndValidateManifestAsync(
        string staging,
        CancellationToken cancellationToken)
    {
        var manifestPath = FindByName(staging, Path.GetFileName(AppPaths.BackupManifestFile));
        if (manifestPath is null)
            return (null, OperationResult.Fail("Снимок извлечён, но recovery manifest не найден."));

        try
        {
            var manifest = await files.LoadAsync<BackupManifest>(manifestPath, () => new BackupManifest(), cancellationToken);
            return (manifest, ValidateRestoredSnapshot(staging, manifest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return (null, OperationResult.Fail("Recovery manifest повреждён или недоступен.", exception.Message));
        }
    }

    public static OperationResult ValidateRestoredSnapshot(string staging, BackupManifest manifest)
    {
        manifest.Projects ??= [];
        manifest.Environment ??= [];
        if (manifest.Projects.Count == 0)
            return OperationResult.Fail("Recovery manifest не содержит проектов.");

        var missingProjects = manifest.Projects.Count(project =>
            LocateRestoredPath(staging, project.SourcePath) is not { } source || !Directory.Exists(source));
        var missingEnvironment = manifest.Environment.Count(item =>
            LocateRestoredPath(staging, item.SourcePath) is null);

        try
        {
            foreach (var item in manifest.Environment)
                _ = ResolveDestinationToken(item.DestinationToken);
        }
        catch (InvalidDataException exception)
        {
            return OperationResult.Fail("Recovery manifest содержит небезопасный путь назначения.", exception.Message);
        }

        if (missingProjects > 0 || missingEnvironment > 0)
            return OperationResult.Fail(
                $"Проверка восстановления не пройдена: отсутствует проектов {missingProjects}, элементов среды {missingEnvironment}.");

        return OperationResult.Ok(
            $"Проверка восстановления пройдена: проектов {manifest.Projects.Count}, элементов среды {manifest.Environment.Count}.");
    }

    private static MergeResult Add(MergeResult left, MergeResult right) => new(
        left.Added + right.Added,
        left.Identical + right.Identical,
        left.Conflicts + right.Conflicts,
        left.Skipped + right.Skipped);

    private static string? FindByName(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? LocateRestoredPath(string staging, string original)
    {
        try
        {
            var full = Path.GetFullPath(original);
            var root = Path.GetPathRoot(full) ?? "";
            var drive = root.TrimEnd('\\', '/').TrimEnd(':');
            var withoutRoot = full[root.Length..].TrimStart('\\', '/');
            var candidates = new[]
            {
                Path.Combine(staging, drive, withoutRoot),
                Path.Combine(staging, withoutRoot),
                Path.Combine(staging, full.Replace(":", "").TrimStart('\\', '/'))
            };

            return candidates.Select(Path.GetFullPath).FirstOrDefault(path =>
                PathPolicy.IsInside(path, staging) && (Directory.Exists(path) || File.Exists(path)));
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveDestinationToken(string token)
    {
        var roots = new[]
        {
            (Token: "{UserProfile}", Path: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            (Token: "{AppData}", Path: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        };

        foreach (var root in roots)
        {
            if (!token.Equals(root.Token, StringComparison.OrdinalIgnoreCase)
                && !token.StartsWith(root.Token + "\\", StringComparison.OrdinalIgnoreCase)
                && !token.StartsWith(root.Token + "/", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = token[root.Token.Length..].TrimStart('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(root.Path, relative));
            if (!string.Equals(destination, Path.GetFullPath(root.Path), StringComparison.OrdinalIgnoreCase)
                && !PathPolicy.IsInside(destination, root.Path))
                break;
            return destination;
        }

        throw new InvalidDataException("Разрешены только пути внутри {UserProfile} и {AppData}.");
    }

    private static string SafeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Project" : safe;
    }

    private static bool IsSafeStagingPath(string path) =>
        PathPolicy.IsInside(path, AppPaths.RestoreDirectory)
        && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(AppPaths.RestoreDirectory), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging) && IsSafeStagingPath(staging))
                Directory.Delete(staging, true);
        }
        catch
        {
            // Остаток во временном каталоге безопаснее, чем ошибка поверх результата восстановления.
        }
    }
}

public static class SafeMergeService
{
    public static async Task<MergeResult> MergeDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        string conflictRoot,
        CancellationToken cancellationToken = default)
    {
        var result = new MergeResult(0, 0, 0, 0);
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(sourceRoot));

        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                result = result with { Skipped = result.Skipped + 1 };
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
                queue.Enqueue(child);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, file);
                var destination = SafeCombine(destinationRoot, relative);
                var conflict = SafeCombine(conflictRoot, relative);
                result = Add(result, await MergeFileAsync(file, destination, conflict, cancellationToken));
            }
        }

        return result;
    }

    public static async Task<MergeResult> MergeFileAsync(
        string source,
        string destination,
        string conflictPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source))
            return new MergeResult(0, 0, 0, 1);

        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
            return new MergeResult(1, 0, 0, 0);
        }

        if (await FilesMatchAsync(source, destination, cancellationToken))
            return new MergeResult(0, 1, 0, 0);

        Directory.CreateDirectory(Path.GetDirectoryName(conflictPath)!);
        File.Copy(source, conflictPath, true);
        return new MergeResult(0, 0, 1, 0);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!PathPolicy.IsInside(combined, fullRoot))
            throw new InvalidDataException("Путь восстановления выходит за разрешённый каталог.");
        return combined;
    }

    private static async Task<bool> FilesMatchAsync(string first, string second, CancellationToken cancellationToken)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length)
            return false;

        await using var firstStream = File.OpenRead(first);
        await using var secondStream = File.OpenRead(second);
        var firstHash = await SHA256.HashDataAsync(firstStream, cancellationToken);
        var secondHash = await SHA256.HashDataAsync(secondStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static MergeResult Add(MergeResult left, MergeResult right) => new(
        left.Added + right.Added,
        left.Identical + right.Identical,
        left.Conflicts + right.Conflicts,
        left.Skipped + right.Skipped);
}
