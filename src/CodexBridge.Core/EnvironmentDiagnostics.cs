using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBridge.Core;

public enum EnvironmentCheckStatus
{
    Ready,
    ActionRequired,
    Optional
}

public sealed record EnvironmentDiagnosticCheck(
    string Name,
    EnvironmentCheckStatus Status,
    string Message)
{
    public string StatusDisplay => Status switch
    {
        EnvironmentCheckStatus.Ready => "Готово",
        EnvironmentCheckStatus.ActionRequired => "Нужно действие",
        _ => "Необязательно"
    };
}

public sealed record ObsidianVaultSummary(int Total, int Existing, int UnderProjectRoots);

public sealed class EnvironmentDiagnosticReport(IReadOnlyList<EnvironmentDiagnosticCheck> checks)
{
    public IReadOnlyList<EnvironmentDiagnosticCheck> Checks { get; } = checks;
    public int ReadyCount => Checks.Count(check => check.Status == EnvironmentCheckStatus.Ready);
    public int ActionRequiredCount => Checks.Count(check => check.Status == EnvironmentCheckStatus.ActionRequired);
    public int OptionalCount => Checks.Count(check => check.Status == EnvironmentCheckStatus.Optional);
    public string Summary =>
        $"Проверка среды завершена: готово {ReadyCount}, требует действий {ActionRequiredCount}, необязательно {OptionalCount}.";
    public string Details => string.Join(Environment.NewLine,
        Checks.Select(check => $"{check.StatusDisplay}: {check.Name} — {check.Message}"));
}

public sealed class EnvironmentDiagnosticsService
{
    private static readonly Regex McpSection = new(
        @"^\s*\[mcp_servers\.([^\.\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task<EnvironmentDiagnosticReport> DiagnoseAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<EnvironmentDiagnosticCheck>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var codexRoot = Path.Combine(profile, ".codex");
        var configPath = Path.Combine(codexRoot, "config.toml");
        var recoveryRoot = Path.Combine(profile, "CodexBridge-Recovery");

        checks.Add(string.IsNullOrWhiteSpace(settings.DestinationRoot)
            ? Required("Единая папка проектов", "не выбрана папка назначения для восстановления")
            : Ready("Единая папка проектов",
                $"выбрана; исходных корней в каталоге: {settings.ProjectRoots.Count}"));

        checks.Add(CheckTool("WinGet", ["winget.exe"], [], true,
            "нужен для повторной установки приложений"));
        checks.Add(CheckTool("restic", [settings.ResticExecutable, "restic.exe"], [], true,
            "нужен для чтения зашифрованных снимков"));
        checks.Add(CheckTool("Git", ["git.exe"],
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe")
        ], true, "после установки будет применён разрешённый Git-профиль"));
        checks.Add(CheckTool("Codex", ["codex.exe", "codex.cmd"], [], true,
            "после установки потребуется повторный вход"));

        IReadOnlyList<string> mcpServers = [];
        try
        {
            if (File.Exists(configPath))
                mcpServers = await ParseMcpServerNamesAsync(configPath, cancellationToken);
            checks.Add(mcpServers.Count == 0
                ? Optional("MCP", "конфигурации не найдены; после восстановления проверка повторится")
                : Required("MCP",
                    $"найдено конфигураций: {mcpServers.Count}; секреты и авторизацию нужно проверить заново"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(Required("MCP", "не удалось прочитать заголовки конфигурации: " + exception.Message));
        }

        var graphify = FindExecutable(["graphify.exe", "graphify.cmd", "graphify"]);
        var gfy = FindExecutable(["gfy.exe", "gfy.cmd", "gfy"]);
        checks.Add(graphify is not null && gfy is not null
            ? Required("Graphify", "CLI и gfy обнаружены; после восстановления нужно зарегистрировать проекты и обновить общий граф")
            : Required("Graphify", "нужно установить CLI и gfy, затем обновить общий граф"));

        var hasCodebaseMemory = mcpServers.Any(name =>
            name.Contains("codebase", StringComparison.OrdinalIgnoreCase)
            || name.Contains("memory", StringComparison.OrdinalIgnoreCase));
        checks.Add(hasCodebaseMemory
            ? Required("Codebase Memory", "MCP-конфигурация обнаружена; проектные индексы нужно пересоздать")
            : Required("Codebase Memory", "MCP-сервер не обнаружен; требуется установка и новая индексация"));

        var ponytailInstalled = Directory.Exists(Path.Combine(codexRoot, "plugins", "cache", "ponytail"))
                                || Directory.Exists(Path.Combine(codexRoot, "skills", "ponytail"));
        checks.Add(ponytailInstalled
            ? Ready("Ponytail", "плагин или пользовательский skill обнаружен")
            : Required("Ponytail", "плагин не обнаружен; кэш намеренно не переносится"));

        var recoveryVaults = Path.Combine(recoveryRoot, "obsidian-vaults.json");
        var liveVaults = Path.Combine(appData, "obsidian", "obsidian.json");
        var vaultRegistry = File.Exists(recoveryVaults) ? recoveryVaults : liveVaults;
        var obsidianInstalled = FindExecutable(["Obsidian.exe"],
            [Path.Combine(localAppData, "Programs", "Obsidian", "Obsidian.exe")]) is not null;
        checks.Add(await BuildObsidianCheckAsync(
            vaultRegistry,
            settings.ProjectRoots.Append(settings.DestinationRoot),
            obsidianInstalled,
            cancellationToken));

        checks.Add(settings.CloudEnabled
            ? CheckTool("rclone", ["rclone.exe"], [], true,
                "нужен для облачной копии; remote и вход проверяются отдельно")
            : CheckTool("rclone", ["rclone.exe"], [], false,
                "понадобится только при включении облачной копии"));

        return new EnvironmentDiagnosticReport(checks);
    }

    public static IReadOnlyList<string> ParseMcpServerNames(string content)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = McpSection.Match(line);
            if (match.Success)
                names.Add(match.Groups[1].Value);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyList<string>> ParseMcpServerNamesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var match = McpSection.Match(line);
            if (match.Success)
                names.Add(match.Groups[1].Value);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<ObsidianVaultSummary> AnalyzeObsidianRegistryAsync(
        string path,
        IEnumerable<string> projectRoots,
        CancellationToken cancellationToken = default)
    {
        var roots = projectRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!File.Exists(path))
            return new ObsidianVaultSummary(0, 0, 0);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("vaults", out var vaults)
            || vaults.ValueKind != JsonValueKind.Object)
            return new ObsidianVaultSummary(0, 0, 0);

        var total = 0;
        var existing = 0;
        var underProjectRoots = 0;
        foreach (var vault in vaults.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!vault.Value.TryGetProperty("path", out var pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(pathElement.GetString()))
                continue;

            total++;
            var vaultPath = Path.GetFullPath(pathElement.GetString()!);
            if (Directory.Exists(vaultPath))
                existing++;
            if (roots.Any(root => PathPolicy.IsInside(vaultPath, root)))
                underProjectRoots++;
        }

        return new ObsidianVaultSummary(total, existing, underProjectRoots);
    }

    private static async Task<EnvironmentDiagnosticCheck> BuildObsidianCheckAsync(
        string registryPath,
        IEnumerable<string> projectRoots,
        bool installed,
        CancellationToken cancellationToken)
    {
        try
        {
            var vaults = await AnalyzeObsidianRegistryAsync(registryPath, projectRoots, cancellationToken);
            if (vaults.Total == 0)
                return installed
                    ? Optional("Obsidian", "программа обнаружена, но сохранённых vault нет")
                    : Optional("Obsidian", "не установлен и сохранённых vault нет");

            if (!installed)
                return Required("Obsidian", $"нужно установить; сохранено vault: {vaults.Total}");

            return vaults.Existing == vaults.Total && vaults.UnderProjectRoots == vaults.Total
                ? Ready("Obsidian", $"vault найдены внутри корней проектов: {vaults.Total}")
                : Required("Obsidian",
                    $"vault: {vaults.Total}; доступно по старому пути: {vaults.Existing}; внутри корней: {vaults.UnderProjectRoots}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Required("Obsidian", "не удалось прочитать реестр vault: " + exception.Message);
        }
    }

    private static EnvironmentDiagnosticCheck CheckTool(
        string name,
        IEnumerable<string> executableNames,
        IEnumerable<string> directCandidates,
        bool required,
        string missingMessage)
    {
        var executable = FindExecutable(executableNames, directCandidates);
        if (executable is not null)
            return Ready(name, "обнаружен");
        return required ? Required(name, missingMessage) : Optional(name, missingMessage);
    }

    private static string? FindExecutable(
        IEnumerable<string> names,
        IEnumerable<string>? directCandidates = null)
    {
        foreach (var candidate in directCandidates ?? [])
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (Path.IsPathRooted(name) && File.Exists(name))
                return name;
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static EnvironmentDiagnosticCheck Ready(string name, string message) =>
        new(name, EnvironmentCheckStatus.Ready, message);

    private static EnvironmentDiagnosticCheck Required(string name, string message) =>
        new(name, EnvironmentCheckStatus.ActionRequired, message);

    private static EnvironmentDiagnosticCheck Optional(string name, string message) =>
        new(name, EnvironmentCheckStatus.Optional, message);
}
