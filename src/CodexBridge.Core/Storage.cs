using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            Item("Codex config", Path.Combine(profile, ".codex", "config.toml"), "{UserProfile}\\.codex\\config.toml"),
            Item("Codex rules", Path.Combine(profile, ".codex", "rules"), "{UserProfile}\\.codex\\rules"),
            Item("Codex memories", Path.Combine(profile, ".codex", "memories"), "{UserProfile}\\.codex\\memories"),
            Item("Legacy Codex skills", Path.Combine(profile, ".codex", "skills"), "{UserProfile}\\.codex\\skills"),
            Item("User agent skills", Path.Combine(profile, ".agents", "skills"), "{UserProfile}\\.agents\\skills"),
            Item("WinGet app inventory", AppPaths.AppInventoryFile, "{UserProfile}\\CodexBridge-Recovery\\winget-packages.json")
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

    public static IReadOnlyList<string> ParseExtensions(string content) =>
        content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('.') && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<OperationResult> CaptureAsync(bool includeVsCode, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
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

        return winget.Succeeded
            ? OperationResult.Ok(vsCode?.Succeeded == true
                ? "Список программ и расширений VS Code обновлён."
                : "Список программ обновлён.", vsCode?.Details ?? vsCode?.Message ?? "")
            : OperationResult.Fail("Не удалось обновить список программ.", winget.Combined);
    }

    public async Task<OperationResult> InstallAppsAsync(bool includeVsCode, CancellationToken cancellationToken = default)
    {
        var inventory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CodexBridge-Recovery", "winget-packages.json");
        if (!File.Exists(inventory))
            return OperationResult.Fail("Восстановленный список WinGet не найден.", inventory);

        var result = await processes.RunAsync("winget.exe",
        [
            "import", "--import-file", inventory, "--ignore-unavailable",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        ], cancellationToken: cancellationToken);

        var vsCode = includeVsCode ? await InstallVsCodeExtensionsAsync(cancellationToken) : null;
        var succeeded = result.Succeeded && (vsCode?.Succeeded ?? true);
        var message = succeeded
            ? includeVsCode ? "Установка приложений и расширений VS Code завершена." : "Установка доступных приложений завершена."
            : "Некоторые приложения или расширения установить не удалось.";
        var details = string.Join(Environment.NewLine, new[] { result.Combined, vsCode?.Details, vsCode?.Message }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return succeeded ? OperationResult.Ok(message, details) : OperationResult.Fail(message, details);
    }

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
        var failures = new List<string>();
        foreach (var extension in extensions)
        {
            var result = await processes.RunAsync(executable, ["--install-extension", extension, "--force"], cancellationToken: cancellationToken);
            if (!result.Succeeded)
                failures.Add(extension);
        }

        return failures.Count == 0
            ? OperationResult.Ok($"Установлено расширений VS Code: {extensions.Count}.")
            : OperationResult.Fail($"Не установлено расширений VS Code: {failures.Count}.", string.Join(Environment.NewLine, failures));
    }
}
