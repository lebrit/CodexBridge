using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodexBridge.Core;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBridge");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string CatalogFile => Path.Combine(DataDirectory, "catalog.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string SecretFile => Path.Combine(DataDirectory, "restic-password.dpapi");
    public static string BackupManifestFile => Path.Combine(DataDirectory, "codexbridge-backup-manifest.json");
    public static string ExcludesFile => Path.Combine(DataDirectory, "restic-excludes.txt");
    public static string ResticCacheDirectory => Path.Combine(DataDirectory, "restic-cache");
    public static string RestoreDirectory => Path.Combine(DataDirectory, "restore");
    public static string RestoreTransactionsDirectory => Path.Combine(DataDirectory, "restore-transactions");
    public static string AppInventoryFile => Path.Combine(DataDirectory, "winget-packages.json");
    public static string VsCodeExtensionsFile => Path.Combine(DataDirectory, "vscode-extensions.txt");
    public static string PortableCodexConfigFile => Path.Combine(DataDirectory, "codex-config-portable.toml");
    public static string GitProfileFile => Path.Combine(DataDirectory, "git-profile.json");
    public static string PortableEnvironmentProfileFile => Path.Combine(DataDirectory, "environment-profile.json");
    public static string BackupLockFile => Path.Combine(DataDirectory, "backup.lock");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ResticCacheDirectory);
        Directory.CreateDirectory(RestoreDirectory);
        Directory.CreateDirectory(RestoreTransactionsDirectory);
    }
}

public sealed class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<T> LoadAsync<T>(string path, Func<T> createDefault, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return createDefault();

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
               ?? createDefault();
    }

    public async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }
}

public sealed class SettingsStore(JsonFileStore files)
{
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        var settings = await files.LoadAsync(AppPaths.SettingsFile, CreateDefaults, cancellationToken);
        settings.ProjectRoots = settings.ProjectRoots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.SchemaVersion = 2;
        settings.KeepDaily = Math.Clamp(settings.KeepDaily, 1, 365);
        settings.KeepWeekly = Math.Clamp(settings.KeepWeekly, 1, 104);
        settings.KeepMonthly = Math.Clamp(settings.KeepMonthly, 1, 120);
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        files.SaveAsync(AppPaths.SettingsFile, settings, cancellationToken);

    private static AppSettings CreateDefaults()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new[]
        {
            documents,
            @"C:\Projects",
            @"D:\Projects",
            @"C:\Codex"
        };

        return new AppSettings
        {
            ProjectRoots = candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LocalRepository = Path.Combine(profile, "CodexBridge-Backups", "restic-v1"),
            DestinationRoot = Directory.Exists(@"D:\") ? @"D:\Projects" : @"C:\Projects"
        };
    }
}

public sealed class CatalogStore(JsonFileStore files)
{
    public Task<List<ProjectEntry>> LoadAsync(CancellationToken cancellationToken = default) =>
        files.LoadAsync<List<ProjectEntry>>(AppPaths.CatalogFile, () => [], cancellationToken);

    public Task SaveAsync(IReadOnlyCollection<ProjectEntry> projects, CancellationToken cancellationToken = default) =>
        files.SaveAsync(AppPaths.CatalogFile, projects.OrderBy(p => p.Name).ThenBy(p => p.Path).ToList(), cancellationToken);
}

public sealed class StateStore(JsonFileStore files)
{
    public Task<BackupState> LoadAsync(CancellationToken cancellationToken = default) =>
        files.LoadAsync(AppPaths.StateFile, () => new BackupState(), cancellationToken);

    public Task SaveAsync(BackupState state, CancellationToken cancellationToken = default) =>
        files.SaveAsync(AppPaths.StateFile, state, cancellationToken);
}

public sealed class DpapiSecretStore
{
    public bool Exists => File.Exists(AppPaths.SecretFile);

    public void Save(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        AppPaths.EnsureCreated();
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppPaths.SecretFile, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string? Load()
    {
        if (!File.Exists(AppPaths.SecretFile))
            return null;

        var protectedBytes = File.ReadAllBytes(AppPaths.SecretFile);
        var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}

public static class BackupFiles
{
    private static readonly string[] Excludes =
    [
        "**/node_modules/**",
        "**/.venv/**",
        "**/bin/**",
        "**/obj/**",
        "**/dist/**",
        "**/build/**",
        "**/coverage/**",
        "**/.cache/**",
        "**/.codex-cache/**",
        "**/.codebase-memory/**",
        "**/.env",
        "**/.env.*",
        "**/*.pem",
        "**/*.pfx",
        "**/*.p12",
        "**/id_rsa*",
        "**/id_ed25519*",
        "**/auth.json",
        "**/state_5.sqlite*",
        "**/restic-password.dpapi"
    ];

    public static IReadOnlyList<string> ExcludedForSafety { get; } =
    [
        "Codex auth.json и OAuth-сессии",
        "активная state_5.sqlite и другие внутренние базы Codex",
        "приватные ключи, .env и файлы сертификатов",
        "локальные индексы Codebase Memory и глобальный индекс Graphify",
        "кэши установленных плагинов"
    ];

    public static IReadOnlyList<string> RequiresManualAction { get; } =
    [
        "повторный вход в Codex, GitHub и облачные сервисы",
        "повторная настройка секретов и переменных окружения MCP",
        "перезапуск Codex после установки инструментов и отдельное доверие к hooks Ponytail",
        "выбор нового пути Obsidian vault, если он не входит в защищаемый проект"
    ];

    public static async Task EnsureExcludesAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        await File.WriteAllLinesAsync(AppPaths.ExcludesFile, Excludes, cancellationToken);
    }

    public static List<ManifestEnvironmentItem> DiscoverEnvironment(bool includeVsCode)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<ManifestEnvironmentItem>
        {
            Item("Portable Codex config", AppPaths.PortableCodexConfigFile, "{UserProfile}\\.codex\\config.toml"),
            Item("Global Codex AGENTS", Path.Combine(profile, ".codex", "AGENTS.md"), "{UserProfile}\\.codex\\AGENTS.md"),
            Item("Global Codex AGENTS override", Path.Combine(profile, ".codex", "AGENTS.override.md"), "{UserProfile}\\.codex\\AGENTS.override.md"),
            Item("Codex rules", Path.Combine(profile, ".codex", "rules"), "{UserProfile}\\.codex\\rules"),
            Item("Codex memories", Path.Combine(profile, ".codex", "memories"), "{UserProfile}\\.codex\\memories"),
            Item("Legacy Codex skills", Path.Combine(profile, ".codex", "skills"), "{UserProfile}\\.codex\\skills"),
            Item("User agent skills", Path.Combine(profile, ".agents", "skills"), "{UserProfile}\\.agents\\skills"),
            Item("WinGet app inventory", AppPaths.AppInventoryFile, "{UserProfile}\\CodexBridge-Recovery\\winget-packages.json"),
            Item("Portable Git profile", AppPaths.GitProfileFile, "{UserProfile}\\CodexBridge-Recovery\\git-profile.json"),
            Item("Portable environment profile", AppPaths.PortableEnvironmentProfileFile, "{UserProfile}\\CodexBridge-Recovery\\environment-profile.json"),
            Item("Obsidian vault registry", Path.Combine(appData, "obsidian", "obsidian.json"), "{UserProfile}\\CodexBridge-Recovery\\obsidian-vaults.json")
        };

        if (includeVsCode)
        {
            candidates.AddRange(
            [
                Item("VS Code settings", Path.Combine(appData, "Code", "User", "settings.json"), "{AppData}\\Code\\User\\settings.json"),
                Item("VS Code keybindings", Path.Combine(appData, "Code", "User", "keybindings.json"), "{AppData}\\Code\\User\\keybindings.json"),
                Item("VS Code snippets", Path.Combine(appData, "Code", "User", "snippets"), "{AppData}\\Code\\User\\snippets"),
                Item("VS Code extensions", AppPaths.VsCodeExtensionsFile, "{UserProfile}\\CodexBridge-Recovery\\vscode-extensions.txt")
            ]);
        }

        return candidates.Where(item => File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath)).ToList();
    }

    private static ManifestEnvironmentItem Item(string name, string source, string destination) => new()
    {
        Name = name,
        SourcePath = source,
        DestinationToken = destination
    };
}

public sealed class ToolInventoryService(ProcessRunner processes)
{
    private RestorePreviewData? cachedRestorePreview;
    private DateTime cachedInventoryWriteUtc;
    private DateTime cachedEnvironmentWriteUtc;
    private DateTimeOffset cachedRestorePreviewUtc;

    private static readonly string[] PortableGitKeys =
    [
        "user.name",
        "user.email",
        "init.defaultBranch",
        "core.autocrlf",
        "core.longpaths",
        "pull.rebase",
        "pull.ff",
        "fetch.prune"
    ];

    private static readonly string[] SensitiveCodexKeyFragments =
    [
        "token", "secret", "password", "api_key", "api-key",
        "authorization", "bearer", "credential", "header"
    ];

    private static readonly string[] PortableEnvironmentVariables =
    [
        "JAVA_HOME",
        "GOPATH",
        "GOBIN",
        "NPM_CONFIG_PREFIX",
        "PYTHONUSERBASE"
    ];

    private const int MaximumUserEnvironmentValueLength = 2047;

    public static string? FindVsCodeExecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new[]
        {
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe"),
            Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe")
        }.FirstOrDefault(File.Exists);
    }

    public static string? FindGitExecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>
        {
            Path.Combine(programFiles, "Git", "cmd", "git.exe"),
            Path.Combine(programFiles, "Git", "bin", "git.exe"),
            Path.Combine(local, "Programs", "Git", "cmd", "git.exe")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "git.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsPortableGitKey(string key) =>
        PortableGitKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static string SanitizeCodexConfig(string content, out int redactedSettings)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var sensitiveSection = false;
        redactedSettings = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var section = trimmed.ToLowerInvariant();
                sensitiveSection = section.Contains("mcp_servers", StringComparison.Ordinal)
                                   && (section.EndsWith(".env]", StringComparison.Ordinal)
                                       || section.EndsWith(".http_headers]", StringComparison.Ordinal)
                                       || section.EndsWith(".env_http_headers]", StringComparison.Ordinal));
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1)
                continue;

            var key = trimmed[..separator].Trim().Trim('"', '\'').ToLowerInvariant();
            var sensitiveKey = key.Equals("env", StringComparison.Ordinal)
                               || SensitiveCodexKeyFragments.Any(key.Contains);
            if (!sensitiveSection && !sensitiveKey)
                continue;

            var indentation = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
            lines[index] = $"{indentation}# CodexBridge: sensitive setting omitted ({key})";
            redactedSettings++;
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static IReadOnlyList<string> ParseExtensions(string content) =>
        content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('.') && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string GetExtensionId(string extension)
    {
        var separator = extension.LastIndexOf('@');
        return separator > 0 ? extension[..separator] : extension;
    }

    public static WingetInventoryPlan BuildWingetInventoryPlan(
        string inventoryJson,
        IEnumerable<string> installedPackageIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryJson);
        var root = JsonNode.Parse(inventoryJson) as JsonObject
                   ?? throw new InvalidDataException("Файл WinGet не содержит JSON-объект.");
        var sources = root["Sources"] as JsonArray
                      ?? throw new InvalidDataException("В файле WinGet отсутствует массив Sources.");
        var installed = new HashSet<string>(installedPackageIds, StringComparer.OrdinalIgnoreCase);
        var requested = new List<string>();
        var alreadyInstalled = new List<string>();
        var pending = new List<string>();

        foreach (var source in sources.OfType<JsonObject>())
        {
            if (source["Packages"] is not JsonArray packages)
                continue;

            for (var index = packages.Count - 1; index >= 0; index--)
            {
                if (packages[index] is not JsonObject package
                    || package["PackageIdentifier"] is not JsonValue identifier
                    || !identifier.TryGetValue<string>(out var packageId)
                    || string.IsNullOrWhiteSpace(packageId))
                {
                    throw new InvalidDataException("Файл WinGet содержит пакет без PackageIdentifier.");
                }

                requested.Add(packageId);
                if (installed.Contains(packageId))
                {
                    alreadyInstalled.Add(packageId);
                    packages.RemoveAt(index);
                }
                else
                {
                    pending.Add(packageId);
                }
            }
        }

        return new WingetInventoryPlan(
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            requested.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            alreadyInstalled.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            pending.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static string? TokenizePortablePath(
        string? value,
        IReadOnlyDictionary<string, string>? roots = null,
        Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        roots ??= CreatePortablePathRoots();
        directoryExists ??= Directory.Exists;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded))
                return null;

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            if (!directoryExists(fullPath))
                return null;

            foreach (var root in roots.OrderByDescending(item => item.Value.Length))
            {
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Value));
                if (!PathsEqual(fullPath, fullRoot) && !PathPolicy.IsInside(fullPath, fullRoot))
                    continue;

                var relative = Path.GetRelativePath(fullRoot, fullPath);
                return relative == "." ? root.Key : root.Key + "\\" + relative;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    public static string? ResolvePortablePathToken(
        string? token,
        IReadOnlyDictionary<string, string>? roots = null,
        Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        roots ??= CreatePortablePathRoots();
        directoryExists ??= Directory.Exists;
        try
        {
            foreach (var root in roots)
            {
                if (!token.Equals(root.Key, StringComparison.OrdinalIgnoreCase)
                    && !token.StartsWith(root.Key + "\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = token.Length == root.Key.Length ? "" : token[(root.Key.Length + 1)..];
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Value));
                var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(fullRoot, relative)));
                if ((!PathsEqual(fullPath, fullRoot) && !PathPolicy.IsInside(fullPath, fullRoot))
                    || !directoryExists(fullPath))
                {
                    return null;
                }

                return fullPath;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    public static PortableEnvironmentPlan BuildPortableEnvironmentPlan(
        PortableEnvironmentProfile profile,
        string? currentUserPath,
        IReadOnlyDictionary<string, string?>? currentUserVariables = null,
        IReadOnlyDictionary<string, string>? roots = null,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        roots ??= CreatePortablePathRoots();
        directoryExists ??= Directory.Exists;
        currentUserVariables ??= PortableEnvironmentVariables.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
            StringComparer.OrdinalIgnoreCase);

        var currentPaths = (currentUserPath ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathsToAdd = new List<string>();
        var existingPaths = 0;
        var rejected = new List<string>();

        foreach (var token in (profile.UserPathEntries ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolvePortablePathToken(token, roots, directoryExists);
            if (resolved is null)
            {
                rejected.Add("PATH: " + token);
                continue;
            }

            if (currentPaths.Contains(resolved))
                existingPaths++;
            else
            {
                pathsToAdd.Add(resolved);
                currentPaths.Add(resolved);
            }
        }

        var variablesToAdd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        foreach (var variable in profile.UserVariables ?? new Dictionary<string, string>())
        {
            if (!PortableEnvironmentVariables.Contains(variable.Key, StringComparer.OrdinalIgnoreCase))
            {
                rejected.Add("переменная: " + variable.Key);
                continue;
            }

            var resolved = ResolvePortablePathToken(variable.Value, roots, directoryExists);
            if (resolved is null)
            {
                rejected.Add(variable.Key + ": " + variable.Value);
                continue;
            }

            currentUserVariables.TryGetValue(variable.Key, out var currentValue);
            if (string.IsNullOrWhiteSpace(currentValue))
                variablesToAdd[variable.Key] = resolved;
            else if (!PathsEqual(NormalizePath(currentValue), resolved))
                conflicts.Add(variable.Key);
        }

        return new PortableEnvironmentPlan(pathsToAdd, existingPaths, variablesToAdd, conflicts, rejected);
    }

    public async Task<OperationResult> CaptureAsync(bool includeVsCode, CancellationToken cancellationToken = default)
    {
        cachedRestorePreview = null;
        AppPaths.EnsureCreated();
        var codex = await CaptureCodexConfigAsync(cancellationToken);
        var gitProfile = await CaptureGitProfileAsync(cancellationToken);
        var environmentProfile = await CaptureEnvironmentProfileAsync(cancellationToken);
        var winget = await processes.RunAsync("winget.exe",
        [
            "export", "--output", AppPaths.AppInventoryFile, "--include-versions",
            "--accept-source-agreements", "--disable-interactivity"
        ], cancellationToken: cancellationToken);

        OperationResult? vsCode = null;
        if (includeVsCode)
        {
            var executable = FindVsCodeExecutable();
            if (executable is null)
            {
                vsCode = OperationResult.Fail("VS Code не обнаружен; его данные пропущены.");
            }
            else
            {
                var code = await processes.RunAsync(executable, ["--list-extensions", "--show-versions"], cancellationToken: cancellationToken);
                if (code.Succeeded)
                    await File.WriteAllTextAsync(AppPaths.VsCodeExtensionsFile, code.Output, cancellationToken);
                vsCode = code.Succeeded
                    ? OperationResult.Ok("Список расширений VS Code обновлён.")
                    : OperationResult.Fail("Не удалось получить список расширений VS Code.", code.Combined);
            }
        }

        var results = new[] { codex, gitProfile, environmentProfile, winget.Succeeded
            ? OperationResult.Ok("Список программ WinGet обновлён.")
            : OperationResult.Fail("Не удалось обновить список программ WinGet.", winget.Combined), vsCode }
            .Where(result => result is not null)
            .Cast<OperationResult>()
            .ToList();
        var details = string.Join(Environment.NewLine, results.Select(result => result.Message)
            .Concat(results.Select(result => result.Details))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return results.All(result => result.Succeeded)
            ? OperationResult.Ok("Переносимый профиль среды обновлён.", details)
            : OperationResult.Fail("Профиль среды обновлён не полностью.", details);
    }

    public async Task<OperationResult> InstallAppsAsync(bool includeVsCode, CancellationToken cancellationToken = default)
    {
        RestorePreviewData preview;
        try
        {
            preview = await BuildRestorePreviewAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return OperationResult.Fail("Не удалось подготовить безопасный план восстановления приложений.", exception.Message);
        }

        ProcessResult? wingetResult = null;
        if (preview.Winget.PendingPackageIds.Count > 0)
        {
            var pendingInventory = Path.Combine(AppPaths.DataDirectory, $"winget-pending-{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(
                    pendingInventory, preview.Winget.PendingInventoryJson, new UTF8Encoding(false), cancellationToken);
                wingetResult = await processes.RunAsync("winget.exe",
                [
                    "import", "--import-file", pendingInventory, "--ignore-unavailable", "--no-upgrade",
                    "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
                ], cancellationToken: cancellationToken);
            }
            finally
            {
                File.Delete(pendingInventory);
            }
        }

        var winget = wingetResult is null
            ? OperationResult.Ok("Все приложения WinGet уже установлены; импорт пропущен.")
            : wingetResult.Succeeded
                ? OperationResult.Ok($"Установлены недостающие приложения WinGet: {preview.Winget.PendingPackageIds.Count}.", wingetResult.Combined)
                : OperationResult.Fail("Некоторые приложения WinGet установить не удалось.", wingetResult.Combined);
        var vsCode = includeVsCode ? await InstallVsCodeExtensionsAsync(cancellationToken) : null;
        var gitProfile = await ApplyGitProfileAsync(cancellationToken);
        var environmentProfile = await ApplyEnvironmentProfileAsync(cancellationToken);
        var succeeded = winget.Succeeded && (vsCode?.Succeeded ?? true) && gitProfile.Succeeded && environmentProfile.Succeeded;
        var message = succeeded
            ? includeVsCode
                ? "Приложения, расширения VS Code и переносимый профиль среды применены."
                : "Приложения и переносимый профиль среды применены."
            : "Некоторые приложения или настройки среды применить не удалось.";
        var details = string.Join(Environment.NewLine,
            new[]
            {
                FormatRestorePreview(preview), winget.Message, winget.Details,
                vsCode?.Message, vsCode?.Details, gitProfile.Message, gitProfile.Details,
                environmentProfile.Message, environmentProfile.Details
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        cachedRestorePreview = null;
        return succeeded ? OperationResult.Ok(message, details) : OperationResult.Fail(message, details);
    }

    public async Task<OperationResult> PreviewRestoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var preview = await BuildRestorePreviewAsync(cancellationToken);
            var installed = preview.InstalledInventoryKnown
                ? preview.Winget.InstalledPackageIds.Count.ToString()
                : "не определено";
            return OperationResult.Ok(
                $"К установке: {preview.Winget.PendingPackageIds.Count}; уже установлено: {installed}.",
                FormatRestorePreview(preview));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return OperationResult.Fail("Не удалось проверить план восстановления приложений.", exception.Message);
        }
    }

    private async Task<OperationResult> CaptureEnvironmentProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(AppPaths.PortableEnvironmentProfileFile);
            var profile = new PortableEnvironmentProfile
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                Runtimes = await ProbeRuntimeVersionsAsync(cancellationToken)
            };

            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            profile.UserPathEntries = userPath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => TokenizePortablePath(path))
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in PortableEnvironmentVariables)
            {
                var token = TokenizePortablePath(
                    Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
                if (token is not null)
                    profile.UserVariables[name] = token;
            }

            await new JsonFileStore().SaveAsync(AppPaths.PortableEnvironmentProfileFile, profile, cancellationToken);
            var versions = string.Join(Environment.NewLine,
                profile.Runtimes.Select(runtime => $"{runtime.Name}: {DisplayVersion(runtime.Version)}"));
            return OperationResult.Ok(
                $"Профиль PATH и версий готов: путей {profile.UserPathEntries.Count}, переменных {profile.UserVariables.Count}.",
                versions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Fail("Не удалось подготовить профиль PATH и версий.", exception.Message);
        }
    }

    private async Task<RestorePreviewData> BuildRestorePreviewAsync(CancellationToken cancellationToken)
    {
        var inventory = RecoveryPath("winget-packages.json");
        if (!File.Exists(inventory))
            throw new FileNotFoundException("Восстановленный список WinGet не найден.", inventory);

        var profilePath = RecoveryPath("environment-profile.json");
        var inventoryWriteUtc = File.GetLastWriteTimeUtc(inventory);
        var environmentWriteUtc = File.Exists(profilePath) ? File.GetLastWriteTimeUtc(profilePath) : DateTime.MinValue;
        if (cachedRestorePreview is not null
            && inventoryWriteUtc == cachedInventoryWriteUtc
            && environmentWriteUtc == cachedEnvironmentWriteUtc
            && DateTimeOffset.UtcNow - cachedRestorePreviewUtc < TimeSpan.FromMinutes(2))
        {
            return cachedRestorePreview;
        }

        var inventoryJson = await File.ReadAllTextAsync(inventory, cancellationToken);
        _ = BuildWingetInventoryPlan(inventoryJson, []);
        var installed = await CaptureInstalledWingetPackageIdsAsync(cancellationToken);
        var winget = BuildWingetInventoryPlan(inventoryJson, installed.PackageIds);

        PortableEnvironmentProfile? profile = null;
        PortableEnvironmentPlan? environmentPlan = null;
        if (File.Exists(profilePath))
        {
            profile = await new JsonFileStore().LoadAsync(
                profilePath, () => new PortableEnvironmentProfile(), cancellationToken);
            if (profile.SchemaVersion != 1)
                throw new InvalidDataException($"Неподдерживаемая версия профиля среды: {profile.SchemaVersion}.");

            environmentPlan = BuildPortableEnvironmentPlan(
                profile,
                Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
        }

        var preview = new RestorePreviewData(
            winget,
            installed.InventoryKnown,
            installed.Warning,
            profile,
            environmentPlan,
            await ProbeRuntimeVersionsAsync(cancellationToken));
        cachedRestorePreview = preview;
        cachedInventoryWriteUtc = inventoryWriteUtc;
        cachedEnvironmentWriteUtc = environmentWriteUtc;
        cachedRestorePreviewUtc = DateTimeOffset.UtcNow;
        return preview;
    }

    private async Task<(IReadOnlyList<string> PackageIds, bool InventoryKnown, string Warning)>
        CaptureInstalledWingetPackageIdsAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        var currentInventory = Path.Combine(AppPaths.DataDirectory, $"winget-current-{Guid.NewGuid():N}.json");
        try
        {
            var export = await processes.RunAsync("winget.exe",
            [
                "export", "--output", currentInventory, "--include-versions",
                "--accept-source-agreements", "--disable-interactivity"
            ], cancellationToken: cancellationToken);
            if (!export.Succeeded || !File.Exists(currentInventory))
            {
                return ([], false,
                    "Текущий список WinGet получить не удалось. Импорт останется безопасным благодаря --no-upgrade.");
            }

            try
            {
                var json = await File.ReadAllTextAsync(currentInventory, cancellationToken);
                var parsed = BuildWingetInventoryPlan(json, []);
                return (parsed.RequestedPackageIds, true, "");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                return ([], false,
                    "Текущий список WinGet не удалось разобрать: " + exception.Message
                    + " Импорт останется безопасным благодаря --no-upgrade.");
            }
        }
        finally
        {
            File.Delete(currentInventory);
        }
    }

    private async Task<List<RuntimeVersionInfo>> ProbeRuntimeVersionsAsync(CancellationToken cancellationToken)
    {
        var python = EnvironmentDiagnosticsService.FindExecutable(["py.exe"]);
        var probes = new (string Name, string? Executable, string[] Arguments)[]
        {
            ("Git", FindGitExecutable(), ["--version"]),
            ("Python", python ?? EnvironmentDiagnosticsService.FindExecutable(["python.exe", "python3.exe"]),
                python is null ? ["--version"] : ["-V"]),
            ("Node.js", EnvironmentDiagnosticsService.FindExecutable(["node.exe", "node"]), ["--version"]),
            ("Java", EnvironmentDiagnosticsService.FindExecutable(["java.exe", "java"]), ["-version"])
        };
        var result = new List<RuntimeVersionInfo>();
        foreach (var probe in probes)
        {
            var version = "";
            if (probe.Executable is not null)
            {
                var process = await processes.RunAsync(
                    probe.Executable, probe.Arguments, cancellationToken: cancellationToken);
                version = FirstNonEmptyLine(process.Combined);
            }

            result.Add(new RuntimeVersionInfo { Name = probe.Name, Version = version });
        }

        return result;
    }

    private async Task<OperationResult> ApplyEnvironmentProfileAsync(CancellationToken cancellationToken)
    {
        var path = RecoveryPath("environment-profile.json");
        if (!File.Exists(path))
            return OperationResult.Ok("Профиль PATH отсутствует; шаг пропущен.");

        try
        {
            var profile = await new JsonFileStore().LoadAsync(
                path, () => new PortableEnvironmentProfile(), cancellationToken);
            if (profile.SchemaVersion != 1)
                return OperationResult.Fail($"Неподдерживаемая версия профиля PATH: {profile.SchemaVersion}.");

            var currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var currentVariables = PortableEnvironmentVariables.ToDictionary(
                name => name,
                name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
                StringComparer.OrdinalIgnoreCase);
            var plan = BuildPortableEnvironmentPlan(profile, currentUserPath, currentVariables);

            var pathParts = currentUserPath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var pathsApplied = 0;
            var pathsTooLong = 0;
            foreach (var item in plan.PathEntriesToAdd)
            {
                var candidate = string.Join(Path.PathSeparator, pathParts.Append(item));
                if (candidate.Length > MaximumUserEnvironmentValueLength)
                {
                    pathsTooLong++;
                    continue;
                }

                pathParts.Add(item);
                pathsApplied++;
            }

            var appliedVariables = new List<string>();
            try
            {
                if (pathsApplied > 0)
                    Environment.SetEnvironmentVariable(
                        "Path", string.Join(Path.PathSeparator, pathParts), EnvironmentVariableTarget.User);
                foreach (var variable in plan.VariablesToAdd)
                {
                    Environment.SetEnvironmentVariable(variable.Key, variable.Value, EnvironmentVariableTarget.User);
                    appliedVariables.Add(variable.Key);
                }
            }
            catch (Exception applyException) when (applyException is UnauthorizedAccessException
                                                             or ArgumentException
                                                             or System.Security.SecurityException)
            {
                var rollbackFailures = new List<string>();
                try
                {
                    if (pathsApplied > 0)
                        Environment.SetEnvironmentVariable("Path", currentUserPath, EnvironmentVariableTarget.User);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add("PATH: " + rollbackException.Message);
                }

                foreach (var name in appliedVariables)
                {
                    try
                    {
                        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackFailures.Add(name + ": " + rollbackException.Message);
                    }
                }

                var rollback = rollbackFailures.Count == 0
                    ? "Применённые в этой операции изменения отменены."
                    : "Ошибки отката: " + string.Join("; ", rollbackFailures);
                return OperationResult.Fail("Не удалось применить переносимый профиль PATH.",
                    applyException.Message + Environment.NewLine + rollback);
            }

            var details = new List<string>();
            if (plan.VariableConflicts.Count > 0)
                details.Add("Сохранены существующие отличающиеся переменные: " + string.Join(", ", plan.VariableConflicts));
            if (plan.RejectedOrMissingEntries.Count > 0)
                details.Add("Пропущены небезопасные или отсутствующие пути: " + string.Join(", ", plan.RejectedOrMissingEntries));
            if (pathsTooLong > 0)
                details.Add($"Не добавлено путей из-за безопасного ограничения длины PATH: {pathsTooLong}.");
            if (pathsApplied > 0 || plan.VariablesToAdd.Count > 0)
                details.Add("Новые значения появятся в приложениях, запущенных после этой операции.");

            return OperationResult.Ok(
                $"PATH: добавлено {pathsApplied}, уже было {plan.ExistingPathEntries}; переменных добавлено {plan.VariablesToAdd.Count}.",
                string.Join(Environment.NewLine, details));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                                   or ArgumentException or System.Security.SecurityException)
        {
            return OperationResult.Fail("Не удалось применить переносимый профиль PATH.", exception.Message);
        }
    }

    private static string FormatRestorePreview(RestorePreviewData preview)
    {
        var installed = preview.InstalledInventoryKnown
            ? preview.Winget.InstalledPackageIds.Count.ToString()
            : "не определено";
        var lines = new List<string>
        {
            $"WinGet: всего {preview.Winget.RequestedPackageIds.Count}; уже установлено {installed}; к установке {preview.Winget.PendingPackageIds.Count}."
        };
        if (!preview.InstalledInventoryKnown && !string.IsNullOrWhiteSpace(preview.Warning))
            lines.Add(preview.Warning);
        if (preview.Winget.PendingPackageIds.Count > 0)
        {
            var shown = string.Join(", ", preview.Winget.PendingPackageIds.Take(12));
            var suffix = preview.Winget.PendingPackageIds.Count > 12 ? " …" : "";
            lines.Add("Будут установлены: " + shown + suffix);
        }

        if (preview.EnvironmentPlan is not null)
        {
            lines.Add(
                $"PATH: добавить {preview.EnvironmentPlan.PathEntriesToAdd.Count}; уже есть {preview.EnvironmentPlan.ExistingPathEntries}; "
                + $"переменных добавить {preview.EnvironmentPlan.VariablesToAdd.Count}; конфликтов {preview.EnvironmentPlan.VariableConflicts.Count}.");
            if (preview.EnvironmentPlan.RejectedOrMissingEntries.Count > 0)
                lines.Add("Недоступные или отклонённые пути: " + string.Join(", ", preview.EnvironmentPlan.RejectedOrMissingEntries));
        }
        else
        {
            lines.Add("Профиль PATH в восстановленных данных отсутствует.");
        }

        if (preview.EnvironmentProfile is not null)
        {
            lines.Add("Версии среды (сохранено → сейчас):");
            foreach (var captured in preview.EnvironmentProfile.Runtimes)
            {
                var current = preview.CurrentRuntimes.FirstOrDefault(
                    item => item.Name.Equals(captured.Name, StringComparison.OrdinalIgnoreCase));
                lines.Add($"  {captured.Name}: {DisplayVersion(captured.Version)} → {DisplayVersion(current?.Version)}");
            }
        }

        lines.Add("Уже установленные приложения и существующие отличающиеся переменные изменяться не будут.");
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<OperationResult> CaptureCodexConfigAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        try
        {
            File.Delete(AppPaths.PortableCodexConfigFile);
            if (!File.Exists(source))
                return OperationResult.Ok("Конфигурация Codex не найдена; шаг пропущен.");

            var sanitized = SanitizeCodexConfig(
                await File.ReadAllTextAsync(source, cancellationToken), out var redacted);
            await File.WriteAllTextAsync(
                AppPaths.PortableCodexConfigFile, sanitized, new UTF8Encoding(false), cancellationToken);
            return OperationResult.Ok($"Переносимая конфигурация Codex готова; скрыто чувствительных значений: {redacted}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("Не удалось подготовить переносимую конфигурацию Codex.", exception.Message);
        }
    }

    private async Task<OperationResult> CaptureGitProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(AppPaths.GitProfileFile);
            var executable = FindGitExecutable();
            if (executable is null)
                return OperationResult.Ok("Git не найден; переносимый Git-профиль пропущен.");

            var profile = new PortableGitProfile { CapturedUtc = DateTimeOffset.UtcNow };
            var failures = new List<string>();
            foreach (var key in PortableGitKeys)
            {
                var result = await processes.RunAsync(
                    executable, ["config", "--global", "--get", key], cancellationToken: cancellationToken);
                if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Output))
                    profile.Settings[key] = result.Output.Trim();
                else if (result.ExitCode != 1)
                    failures.Add(key);
            }

            await new JsonFileStore().SaveAsync(AppPaths.GitProfileFile, profile, cancellationToken);
            return failures.Count == 0
                ? OperationResult.Ok($"Переносимый Git-профиль готов; параметров: {profile.Settings.Count}.")
                : OperationResult.Fail("Некоторые разрешённые Git-настройки прочитать не удалось.", string.Join(", ", failures));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("Не удалось подготовить переносимый Git-профиль.", exception.Message);
        }
    }

    private async Task<OperationResult> ApplyGitProfileAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CodexBridge-Recovery", "git-profile.json");
        if (!File.Exists(path))
            return OperationResult.Ok("Восстановленный Git-профиль отсутствует; шаг пропущен.");

        var executable = FindGitExecutable();
        if (executable is null)
            return OperationResult.Fail("Git не установлен; переносимый профиль не применён.");

        PortableGitProfile profile;
        try
        {
            profile = await new JsonFileStore().LoadAsync(path, () => new PortableGitProfile(), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Fail("Не удалось прочитать восстановленный Git-профиль.", exception.Message);
        }

        var applied = 0;
        var unchanged = 0;
        var rejected = 0;
        var failures = new List<string>();
        foreach (var setting in profile.Settings ?? new Dictionary<string, string>())
        {
            if (!IsPortableGitKey(setting.Key) || !IsPortableGitValue(setting.Value))
            {
                rejected++;
                continue;
            }

            var current = await processes.RunAsync(
                executable, ["config", "--global", "--get", setting.Key], cancellationToken: cancellationToken);
            if (current.Succeeded && current.Output.Trim().Equals(setting.Value, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }
            if (!current.Succeeded && current.ExitCode != 1)
            {
                failures.Add(setting.Key + " (чтение)");
                continue;
            }

            var result = await processes.RunAsync(
                executable, ["config", "--global", "--replace-all", setting.Key, setting.Value],
                cancellationToken: cancellationToken);
            if (result.Succeeded)
                applied++;
            else
                failures.Add(setting.Key);
        }

        var summary = $"Git-настройки: изменено {applied}; уже совпадало {unchanged}; отклонено небезопасных {rejected}.";
        return failures.Count == 0
            ? OperationResult.Ok(summary)
            : OperationResult.Fail(summary, "Ошибки: " + string.Join(", ", failures));
    }

    private static bool IsPortableGitValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 1024
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;

    private async Task<OperationResult> InstallVsCodeExtensionsAsync(CancellationToken cancellationToken)
    {
        var inventory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CodexBridge-Recovery", "vscode-extensions.txt");
        if (!File.Exists(inventory))
            return OperationResult.Ok("Список расширений VS Code отсутствует; шаг пропущен.");

        var executable = FindVsCodeExecutable();
        if (executable is null)
            return OperationResult.Fail("VS Code не установлен; расширения не восстановлены.");

        var extensions = ParseExtensions(await File.ReadAllTextAsync(inventory, cancellationToken));
        var current = await processes.RunAsync(
            executable, ["--list-extensions", "--show-versions"], cancellationToken: cancellationToken);
        var installedIds = current.Succeeded
            ? ParseExtensions(current.Output).Select(GetExtensionId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = extensions
            .Where(extension => !installedIds.Contains(GetExtensionId(extension)))
            .ToList();
        var failures = new List<string>();
        foreach (var extension in pending)
        {
            var result = await processes.RunAsync(
                executable, ["--install-extension", extension], cancellationToken: cancellationToken);
            if (!result.Succeeded)
                failures.Add(extension);
        }

        return failures.Count == 0
            ? OperationResult.Ok($"Расширения VS Code: установлено {pending.Count}, уже было {extensions.Count - pending.Count}.",
                current.Succeeded ? "" : "Текущий список расширений прочитать не удалось; установка выполнена без --force.")
            : OperationResult.Fail($"Не установлено расширений VS Code: {failures.Count}.", string.Join(Environment.NewLine, failures));
    }

    private static Dictionary<string, string> CreatePortablePathRoots()
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, "{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(roots, "{AppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, "{ProgramFilesX86}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddRoot(roots, "{ProgramFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(roots, "{UserProfile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return roots;
    }

    private static void AddRoot(IDictionary<string, string> roots, string token, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            roots[token] = Path.GetFullPath(path);
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Path.IsPathFullyQualified(expanded)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded))
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null && right is not null
        && Path.TrimEndingDirectorySeparator(left).Equals(
            Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmptyLine(string? value)
    {
        var line = (value ?? "")
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var safe = new string(line.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return safe.Length <= 240 ? safe : safe[..240];
    }

    private static string DisplayVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "не обнаружен" : version;

    private static string RecoveryPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CodexBridge-Recovery", fileName);

    private sealed record RestorePreviewData(
        WingetInventoryPlan Winget,
        bool InstalledInventoryKnown,
        string Warning,
        PortableEnvironmentProfile? EnvironmentProfile,
        PortableEnvironmentPlan? EnvironmentPlan,
        IReadOnlyList<RuntimeVersionInfo> CurrentRuntimes);
}
