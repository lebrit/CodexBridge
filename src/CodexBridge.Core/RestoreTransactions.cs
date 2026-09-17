using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBridge.Core;

public sealed class RestoreTransactionStore
{
    private const string HeaderFileName = "transaction.json";
    private const string JournalFileName = "files.jsonl";
    private readonly string root;
    private readonly JsonFileStore files = new();
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RestoreTransactionStore(string? root = null)
    {
        this.root = Path.GetFullPath(root ?? AppPaths.RestoreTransactionsDirectory);
    }

    public string GetTransactionDirectory(string transactionId) =>
        TransactionDirectory(ValidateId(transactionId));

    internal static string GetPartialPath(string target, string transactionId) =>
        $"{target}.codexbridge-{ValidateId(transactionId)}.partial";

    public async Task<RestoreTransaction> BeginAsync(
        string snapshotId,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        var fullDestination = Path.GetFullPath(destinationRoot);
        var transaction = new RestoreTransaction
        {
            Id = id,
            SnapshotId = snapshotId.Trim(),
            DestinationRoot = fullDestination,
            ConflictRoot = Path.Combine(fullDestination, ".codexbridge-conflicts", id),
            StartedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Status = RestoreTransactionStatus.InProgress,
            Message = "Восстановление выполняется.",
            JournalRoot = root
        };

        Directory.CreateDirectory(TransactionDirectory(id));
        await SaveAsync(transaction, cancellationToken);
        return transaction;
    }

    public async Task RecordWriteIntentAsync(
        RestoreTransaction transaction,
        RestoreMutationKind kind,
        string source,
        string target,
        CancellationToken cancellationToken = default)
    {
        var fullTarget = ValidateTarget(transaction, kind, target);
        await using var sourceStream = File.OpenRead(source);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream, cancellationToken));
        var entry = new RestoreJournalEntry(
            transaction.RecordedFiles + 1,
            kind,
            fullTarget,
            hash,
            new FileInfo(source).Length,
            DateTimeOffset.UtcNow);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JournalOptions);
        var journal = JournalPath(transaction.Id);
        await using var stream = new FileStream(
            journal, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        transaction.RecordedFiles++;
        transaction.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public async Task CompleteAsync(
        RestoreTransaction transaction,
        MergeResult result,
        CancellationToken cancellationToken = default)
    {
        transaction.Status = RestoreTransactionStatus.Completed;
        transaction.UpdatedUtc = DateTimeOffset.UtcNow;
        transaction.Message = $"Добавлено {result.Added}, конфликтов {result.Conflicts}. Можно выполнить проверенный откат.";
        await SaveAsync(transaction, cancellationToken);
    }

    public async Task MarkInterruptedAsync(
        RestoreTransaction transaction,
        string message,
        CancellationToken cancellationToken = default)
    {
        transaction.Status = RestoreTransactionStatus.InProgress;
        transaction.UpdatedUtc = DateTimeOffset.UtcNow;
        transaction.Message = message.Trim();
        await SaveAsync(transaction, cancellationToken);
    }

    public async Task<IReadOnlyList<RestoreTransaction>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
            return [];

        var result = new List<RestoreTransaction>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = Path.Combine(directory, HeaderFileName);
            if (!File.Exists(header))
                continue;

            try
            {
                var transaction = await files.LoadAsync<RestoreTransaction>(header, () => new(), cancellationToken);
                if (string.IsNullOrWhiteSpace(transaction.Id))
                    continue;
                transaction.JournalRoot = root;
                transaction.RecordedFiles = await CountJournalEntriesAsync(transaction.Id, cancellationToken);
                result.Add(transaction);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                ErrorLog.Write("Чтение журнала восстановления", exception.Message, header);
            }
        }

        return result.OrderByDescending(item => item.StartedUtc).ToList();
    }

    public async Task<OperationResult> RollbackAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        var transaction = await LoadAsync(transactionId, cancellationToken);
        if (transaction is null)
            return OperationResult.Fail("Журнал восстановления не найден.");
        if (!transaction.CanRollback)
            return OperationResult.Fail("Эта транзакция уже полностью откачена.");

        transaction.Status = RestoreTransactionStatus.RollbackInProgress;
        transaction.UpdatedUtc = DateTimeOffset.UtcNow;
        transaction.Message = "Выполняется проверенный откат.";
        await SaveAsync(transaction, cancellationToken);

        var entries = await ReadJournalAsync(transaction.Id, cancellationToken);
        var removed = 0;
        var skipped = 0;
        foreach (var entry in entries.OrderByDescending(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ValidateTarget(transaction, entry.Kind, entry.TargetPath);
            var partial = ValidateTarget(
                transaction, entry.Kind, GetPartialPath(target, transaction.Id));
            if (File.Exists(partial))
                File.Delete(partial);
            if (!File.Exists(target))
                continue;

            var info = new FileInfo(target);
            if (info.Length != entry.Length || !await HashMatchesAsync(target, entry.Sha256, cancellationToken))
            {
                skipped++;
                continue;
            }

            File.Delete(target);
            removed++;
        }

        transaction.RecordedFiles = entries.Count;
        transaction.UpdatedUtc = DateTimeOffset.UtcNow;
        transaction.Status = skipped == 0
            ? RestoreTransactionStatus.RolledBack
            : RestoreTransactionStatus.RollbackBlocked;
        transaction.Message = skipped == 0
            ? $"Откат завершён: удалено созданных файлов {removed}."
            : $"Откат частичный: удалено {removed}, сохранено изменённых файлов {skipped}.";
        await SaveAsync(transaction, cancellationToken);
        TryDeleteEmptyConflictRoot(transaction.ConflictRoot);

        return skipped == 0
            ? OperationResult.Ok(transaction.Message, HeaderPath(transaction.Id))
            : OperationResult.Fail(
                transaction.Message + " Изменённые после восстановления файлы не удалены.",
                HeaderPath(transaction.Id));
    }

    private async Task<RestoreTransaction?> LoadAsync(string transactionId, CancellationToken cancellationToken)
    {
        var id = ValidateId(transactionId);
        var path = HeaderPath(id);
        if (!File.Exists(path))
            return null;
        var transaction = await files.LoadAsync<RestoreTransaction>(path, () => new(), cancellationToken);
        if (!string.Equals(transaction.Id, id, StringComparison.Ordinal))
            throw new InvalidDataException("Идентификатор журнала восстановления не совпадает с каталогом.");
        transaction.JournalRoot = root;
        transaction.RecordedFiles = await CountJournalEntriesAsync(id, cancellationToken);
        return transaction;
    }

    private Task SaveAsync(RestoreTransaction transaction, CancellationToken cancellationToken) =>
        files.SaveAsync(HeaderPath(transaction.Id), transaction, cancellationToken);

    private async Task<int> CountJournalEntriesAsync(string transactionId, CancellationToken cancellationToken)
    {
        var path = JournalPath(transactionId);
        if (!File.Exists(path))
            return 0;

        var count = 0;
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        }
        return count;
    }

    private async Task<List<RestoreJournalEntry>> ReadJournalAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        var path = JournalPath(transactionId);
        if (!File.Exists(path))
            return [];

        var result = new List<RestoreJournalEntry>();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize<RestoreJournalEntry>(line, JournalOptions)
                            ?? throw new InvalidDataException("Журнал восстановления содержит пустую запись.");
                result.Add(entry);
            }
            catch (JsonException) when (index == lines.Length - 1)
            {
                ErrorLog.Write(
                    "Чтение журнала восстановления",
                    "Последняя незавершённая запись журнала пропущена.",
                    path);
            }
        }
        return result;
    }

    private string ValidateTarget(RestoreTransaction transaction, RestoreMutationKind kind, string target)
    {
        var full = Path.GetFullPath(target);
        var allowed = kind == RestoreMutationKind.ConflictFile
            ? PathPolicy.IsInside(full, transaction.ConflictRoot)
            : IsAllowedDestination(full, transaction.DestinationRoot);
        if (!allowed)
            throw new InvalidDataException("Журнал восстановления содержит путь вне разрешённых каталогов.");
        return full;
    }

    private static bool IsAllowedDestination(string path, string destinationRoot)
    {
        if (PathPolicy.IsInside(path, destinationRoot))
            return true;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return PathPolicy.IsInside(path, userProfile) || PathPolicy.IsInside(path, appData);
    }

    private static async Task<bool> HashMatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static void TryDeleteEmptyConflictRoot(string conflictRoot)
    {
        try
        {
            if (Directory.Exists(conflictRoot) && !Directory.EnumerateFileSystemEntries(conflictRoot).Any())
                Directory.Delete(conflictRoot);
        }
        catch
        {
            // Пустой служебный каталог безопасно оставить для диагностики.
        }
    }

    private string HeaderPath(string transactionId) =>
        Path.Combine(TransactionDirectory(ValidateId(transactionId)), HeaderFileName);

    private string JournalPath(string transactionId) =>
        Path.Combine(TransactionDirectory(ValidateId(transactionId)), JournalFileName);

    private string TransactionDirectory(string transactionId) =>
        Path.Combine(root, ValidateId(transactionId));

    private static string ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Некорректный идентификатор журнала восстановления.");
        return value;
    }

    private sealed record RestoreJournalEntry(
        int Sequence,
        RestoreMutationKind Kind,
        string TargetPath,
        string Sha256,
        long Length,
        DateTimeOffset PlannedUtc);
}
