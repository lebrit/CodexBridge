using System.Security.Cryptography;

namespace CodexBridge.Core;

public sealed class RestoreService(
    ResticService restic,
    JsonFileStore files,
    RestoreTransactionStore? transactionStore = null)
{
    private readonly RestoreTransactionStore transactions = transactionStore ?? new RestoreTransactionStore();

    public Task<OperationResult> RestoreSnapshotAsync(
        AppSettings settings,
        string repository,
        string password,
        string snapshot,
        string destinationRoot,
        CancellationToken cancellationToken = default) =>
        ProcessSnapshotAsync(settings, repository, password, snapshot, destinationRoot,
            verify: false, apply: true, cancellationToken);

    public Task<OperationResult> PlanRestoreAsync(
        AppSettings settings,
        string repository,
        string password,
        string snapshot,
        string destinationRoot,
        CancellationToken cancellationToken = default) =>
        ProcessSnapshotAsync(settings, repository, password, snapshot, destinationRoot,
            verify: true, apply: false, cancellationToken);

    private async Task<OperationResult> ProcessSnapshotAsync(
        AppSettings settings,
        string repository,
        string password,
        string snapshot,
        string destinationRoot,
        bool verify,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return OperationResult.Fail("Выберите папку восстановления.");

        var staging = Path.Combine(AppPaths.RestoreDirectory, Guid.NewGuid().ToString("N"));
        RestoreTransaction? transaction = null;
        try
        {
            var extract = await restic.RestoreRawAsync(
                settings.ResticExecutable, repository, password, snapshot, staging,
                verify, cancellationToken);
            if (!extract.Succeeded)
                return apply
                    ? extract
                    : OperationResult.Fail(
                        "Проверка восстановления не пройдена: снимок не удалось полностью извлечь и проверить.",
                        extract.Details);

            var prepared = await LoadAndValidateManifestAsync(staging, cancellationToken);
            if (!prepared.Result.Succeeded || prepared.Manifest is null)
                return prepared.Result;

            var manifest = prepared.Manifest;
            var fullDestinationRoot = Path.GetFullPath(destinationRoot);
            if (apply)
            {
                Directory.CreateDirectory(fullDestinationRoot);
                transaction = await transactions.BeginAsync(snapshot, fullDestinationRoot, cancellationToken);
            }
            var runName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var conflictRoot = transaction?.ConflictRoot
                               ?? Path.Combine(fullDestinationRoot, ".codexbridge-conflicts", runName);
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

                var destination = Path.Combine(fullDestinationRoot, uniqueName);
                var result = apply
                    ? await SafeMergeService.MergeDirectoryAsync(
                        source, destination, Path.Combine(conflictRoot, uniqueName), transaction!, cancellationToken)
                    : await SafeMergeService.PlanDirectoryAsync(source, destination, cancellationToken);
                total = Add(total, result);
            }

            foreach (var item in manifest.Environment)
            {
                var source = LocateRestoredPath(staging, item.SourcePath);
                if (source is null)
                    continue;

                var destination = ResolveDestinationToken(item.DestinationToken);
                var conflicts = Path.Combine(conflictRoot, "environment", SafeDirectoryName(item.Name));
                var result = Directory.Exists(source)
                    ? apply
                        ? await SafeMergeService.MergeDirectoryAsync(
                            source, destination, conflicts, transaction!, cancellationToken)
                        : await SafeMergeService.PlanDirectoryAsync(source, destination, cancellationToken)
                    : apply
                        ? await SafeMergeService.MergeFileAsync(
                            source, destination, Path.Combine(conflicts, Path.GetFileName(destination)),
                            transaction!, cancellationToken)
                        : await SafeMergeService.PlanFileAsync(source, destination, cancellationToken);
                total = Add(total, result);
            }

            var counts = $"добавлено {total.Added}, совпало {total.Identical}, конфликтов {total.Conflicts}, пропущено {total.Skipped}";
            if (transaction is not null)
                await transactions.CompleteAsync(transaction, total, cancellationToken);
            return apply
                ? OperationResult.Ok(
                    $"Восстановление завершено: {counts}. Журнал транзакции сохранён; доступен проверенный откат.",
                    $"Конфликты: {conflictRoot}{Environment.NewLine}Журнал: {transactions.GetTransactionDirectory(transaction!.Id)}")
                : OperationResult.Ok(
                    $"Проверка и dry-run завершены: будет {counts}. Рабочие папки не изменялись.",
                    extract.Details);
        }
        catch (OperationCanceledException) when (transaction is not null)
        {
            var rollback = await RollbackAfterFailureAsync(transaction, "Операция отменена; выполняется автоматический откат.");
            if (!rollback.Succeeded)
                throw new InvalidOperationException(rollback.Message);
            throw;
        }
        catch (Exception exception) when (transaction is not null)
        {
            var rollback = await RollbackAfterFailureAsync(
                transaction, $"Восстановление прервано: {exception.Message}");
            throw new InvalidOperationException($"{exception.Message} {rollback.Message}", exception);
        }
        finally
        {
            TryDeleteStaging(staging);
        }
    }

    public Task<IReadOnlyList<RestoreTransaction>> ListTransactionsAsync(
        CancellationToken cancellationToken = default) =>
        transactions.ListAsync(cancellationToken);

    public Task<OperationResult> RollbackAsync(
        string transactionId,
        CancellationToken cancellationToken = default) =>
        transactions.RollbackAsync(transactionId, cancellationToken);

    private async Task<OperationResult> RollbackAfterFailureAsync(
        RestoreTransaction transaction,
        string reason)
    {
        try
        {
            await transactions.MarkInterruptedAsync(transaction, reason, CancellationToken.None);
            return await transactions.RollbackAsync(transaction.Id, CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            ErrorLog.Write("Автоматический откат восстановления", rollbackException.Message, rollbackException.ToString());
            return OperationResult.Fail(
                "Автоматический откат не завершён. Откройте журнал восстановления и повторите откат.",
                rollbackException.Message);
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
    public static Task<MergeResult> PlanDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default) =>
        ProcessDirectoryAsync(sourceRoot, destinationRoot, null, null, null, cancellationToken);

    public static Task<MergeResult> MergeDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        string conflictRoot,
        CancellationToken cancellationToken = default) =>
        ProcessDirectoryAsync(sourceRoot, destinationRoot, conflictRoot, null, null, cancellationToken);

    public static Task<MergeResult> MergeDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        string conflictRoot,
        RestoreTransaction transaction,
        CancellationToken cancellationToken = default) =>
        ProcessDirectoryAsync(
            sourceRoot, destinationRoot, conflictRoot, transaction,
            new RestoreTransactionStore(transaction.JournalRoot), cancellationToken);

    private static async Task<MergeResult> ProcessDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        string? conflictRoot,
        RestoreTransaction? transaction,
        RestoreTransactionStore? transactionStore,
        CancellationToken cancellationToken)
    {
        var result = new MergeResult(0, 0, 0, 0);
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(sourceRoot));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                var fileResult = conflictRoot is null
                    ? await PlanFileAsync(file, destination, cancellationToken)
                    : transaction is null
                        ? await MergeFileAsync(file, destination, SafeCombine(conflictRoot, relative), cancellationToken)
                        : await MergeFileCoreAsync(
                            file, destination, SafeCombine(conflictRoot, relative),
                            transaction, transactionStore, cancellationToken);
                result = Add(result, fileResult);
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
        return await MergeFileCoreAsync(source, destination, conflictPath, null, null, cancellationToken);
    }

    public static Task<MergeResult> MergeFileAsync(
        string source,
        string destination,
        string conflictPath,
        RestoreTransaction transaction,
        CancellationToken cancellationToken = default) =>
        MergeFileCoreAsync(
            source, destination, conflictPath, transaction,
            new RestoreTransactionStore(transaction.JournalRoot), cancellationToken);

    private static async Task<MergeResult> MergeFileCoreAsync(
        string source,
        string destination,
        string conflictPath,
        RestoreTransaction? transaction,
        RestoreTransactionStore? transactionStore,
        CancellationToken cancellationToken)
    {
        var result = await PlanFileAsync(source, destination, cancellationToken);
        if (result.Added > 0)
        {
            if (transaction is not null)
                await transactionStore!.RecordWriteIntentAsync(
                    transaction, RestoreMutationKind.AddedFile, source, destination, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFile(source, destination, transaction);
        }
        else if (result.Conflicts > 0)
        {
            if (transaction is not null)
                await transactionStore!.RecordWriteIntentAsync(
                    transaction, RestoreMutationKind.ConflictFile, source, conflictPath, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(conflictPath)!);
            CopyFile(source, conflictPath, transaction);
        }

        return result;
    }

    private static void CopyFile(string source, string target, RestoreTransaction? transaction)
    {
        if (transaction is null)
        {
            File.Copy(source, target, false);
            return;
        }

        var partial = RestoreTransactionStore.GetPartialPath(target, transaction.Id);
        File.Copy(source, partial, false);
        File.Move(partial, target, false);
    }

    public static async Task<MergeResult> PlanFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source))
            return new MergeResult(0, 0, 0, 1);
        if (!File.Exists(destination))
            return new MergeResult(1, 0, 0, 0);
        return await FilesMatchAsync(source, destination, cancellationToken)
            ? new MergeResult(0, 1, 0, 0)
            : new MergeResult(0, 0, 1, 0);
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
