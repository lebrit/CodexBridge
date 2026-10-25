using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexBridge.Core;

public sealed class EnvironmentAutomationService(ProcessRunner processes)
{
    private static readonly string[] SkippedVaultDirectories =
        [".git", ".codexbridge-conflicts", "bin", "obj", "node_modules", ".venv"];

    public async Task<OperationResult> PrepareAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        var failures = 0;
        var changes = 0;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var codexRoot = Path.Combine(profile, ".codex");
        var winget = Find(["winget.exe"]);

        async Task<bool> RunStepAsync(
            string name,
            string executable,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            bool countFailure = true)
        {
            var result = await RunToolAsync(executable, arguments, cancellationToken, workingDirectory);
            if (result.Succeeded)
            {
                changes++;
                details.Add($"Готово: {name}.");
                return true;
            }

            if (countFailure)
                failures++;
            details.Add($"{(countFailure ? "Ошибка" : "Предупреждение")}: {name} — {ShortError(result)}");
            return false;
        }

        async Task<bool> EnsureWingetAsync(
            string name,
            string packageId,
            Func<bool> isInstalled)
        {
            if (isInstalled())
            {
                details.Add($"Уже готово: {name}.");
                return true;
            }

            if (winget is null)
            {
                failures++;
                details.Add($"Ошибка: {name} не установлен, а WinGet недоступен.");
                return false;
            }

            return await RunStepAsync(name, winget,
            [
                "install", "--id", packageId, "--exact", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity"
            ]);
        }

        await EnsureWingetAsync("restic", "restic.restic", () => FindRestic(settings) is not null);
        if (settings.CloudEnabled)
            await EnsureWingetAsync("rclone", "Rclone.Rclone", () => FindRclone() is not null);
        await EnsureWingetAsync("Git", "Git.Git", () => FindGit() is not null);

        var ponytailInstalled = IsPonytailInstalled(codexRoot);
        var configPath = Path.Combine(codexRoot, "config.toml");
        IReadOnlyList<string> mcpServers = [];
        try
        {
            if (File.Exists(configPath))
                mcpServers = await EnvironmentDiagnosticsService.ParseMcpServerNamesAsync(configPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures++;
            details.Add("Ошибка: не удалось безопасно проверить MCP-конфигурацию — " + exception.Message);
        }
        var codebaseConfigured = mcpServers.Any(IsCodebaseMemoryName);
        var codebaseExecutable = FindCodebaseMemory(profile, appData);
        var codexExecutable = FindCodex(localAppData, appData);
        var needNode = codebaseExecutable is null || codexExecutable is null || !ponytailInstalled;
        if (needNode)
            await EnsureWingetAsync("Node.js LTS", "OpenJS.NodeJS.LTS", () => FindNode() is not null);

        var npm = FindNpm(appData);
        if (codexExecutable is null)
        {
            if (npm is null)
            {
                failures++;
                details.Add("Ошибка: Codex не установлен и npm недоступен после установки Node.js.");
            }
            else
            {
                await RunStepAsync("Codex CLI", npm, ["install", "-g", "@openai/codex@latest"]);
                codexExecutable = FindCodex(localAppData, appData);
            }
        }
        else
        {
            details.Add("Уже готово: Codex CLI.");
        }

        var graphify = FindGraphify(profile);
        var uv = FindUv(profile, localAppData);
        if (graphify is null && uv is null)
        {
            await EnsureWingetAsync("uv", "astral-sh.uv", () => FindUv(profile, localAppData) is not null);
            uv = FindUv(profile, localAppData);
        }

        if (graphify is null)
        {
            if (uv is null)
            {
                failures++;
                details.Add("Ошибка: Graphify не установлен и uv недоступен.");
            }
            else if (await RunStepAsync("Graphify CLI", uv, ["tool", "install", "--upgrade", "graphifyy"]))
            {
                graphify = FindGraphify(profile);
            }
        }
        else
        {
            details.Add("Уже готово: Graphify CLI.");
        }

        var graphifySkill = Path.Combine(codexRoot, "skills", "graphify", "SKILL.md");
        if (!File.Exists(graphifySkill))
        {
            if (graphify is not null)
                await RunStepAsync("skill Graphify для Codex", graphify, ["install", "--platform", "codex"]);
            else if (uv is not null)
                await RunStepAsync("skill Graphify для Codex", uv,
                    ["tool", "run", "--from", "graphifyy", "graphify", "install", "--platform", "codex"]);
        }
        else
        {
            details.Add("Уже готово: skill Graphify для Codex.");
        }

        if (codebaseExecutable is null)
        {
            if (npm is null)
            {
                failures++;
                details.Add("Ошибка: Codebase Memory не установлен и npm недоступен.");
            }
            else if (await RunStepAsync("Codebase Memory", npm,
                         ["install", "-g", "codebase-memory-mcp@latest"]))
            {
                codebaseExecutable = FindCodebaseMemory(profile, appData);
            }
        }
        else
        {
            details.Add("Уже готово: Codebase Memory.");
        }

        if (!codebaseConfigured)
        {
            if (codebaseExecutable is null)
            {
                failures++;
                details.Add("Ошибка: Codebase Memory установлен, но его команда не найдена для регистрации.");
            }
            else
            {
                await RunStepAsync("регистрация Codebase Memory в Codex", codebaseExecutable,
                    ["install", "-y"]);
            }
        }
        else
        {
            details.Add("Уже готово: Codebase Memory зарегистрирован в MCP.");
        }

        if (!ponytailInstalled)
        {
            codexExecutable ??= FindCodex(localAppData, appData);
            if (codexExecutable is null)
            {
                failures++;
                details.Add("Ошибка: Ponytail нельзя установить, пока команда Codex недоступна.");
            }
            else
            {
                await RunStepAsync("источник плагина Ponytail", codexExecutable,
                    ["plugin", "marketplace", "add", "DietrichGebert/ponytail"], countFailure: false);
                await RunStepAsync("плагин Ponytail", codexExecutable,
                    ["plugin", "add", "ponytail@ponytail"]);
            }
        }
        else
        {
            details.Add("Уже готово: Ponytail.");
        }

        var recoveryVaults = Path.Combine(profile, "CodexBridge-Recovery", "obsidian-vaults.json");
        if (File.Exists(recoveryVaults))
        {
            await EnsureWingetAsync("Obsidian", "Obsidian.Obsidian",
                () => FindObsidian(localAppData) is not null);
        }

        details.Add("Входы, токены, OAuth-сессии и доверие к hooks не переносились. После изменений перезапустите Codex и проверьте их вручную.");
        var summary = failures == 0
            ? changes == 0
                ? "Среда уже подготовлена; изменений не потребовалось."
                : $"Автоподготовка завершена: выполнено действий {changes}."
            : $"Автоподготовка завершена частично: выполнено {changes}, ошибок {failures}.";
        return failures == 0
            ? OperationResult.Ok(summary, string.Join(Environment.NewLine, details))
            : OperationResult.Fail(summary, string.Join(Environment.NewLine, details));
    }

    public async Task<OperationResult> RebuildIndexesAsync(
        IEnumerable<ProjectEntry> projects,
        CancellationToken cancellationToken = default)
    {
        var targets = GetIndexableProjects(projects);
        if (targets.Count == 0)
            return OperationResult.Fail("Нет защищённых проектов с доступными папками для индексации.");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var graphify = FindGraphify(profile);
        var codebase = FindCodebaseMemory(profile, appData);
        var gfy = FindGfy(profile);
        if (graphify is null && codebase is null)
            return OperationResult.Fail("Сначала установите Graphify или Codebase Memory кнопкой автоподготовки.");

        var graphifyReady = 0;
        var codebaseReady = 0;
        var failures = new List<string>();
        if (graphify is null)
            failures.Add("Graphify: команда не найдена, его индексы пропущены.");
        if (codebase is null)
            failures.Add("Codebase Memory: команда не найдена, его индексы пропущены.");
        foreach (var project in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graphifySucceeded = false;
            if (graphify is not null)
            {
                var graphPath = Path.Combine(project.Path, "graphify-out", "graph.json");
                var arguments = File.Exists(graphPath)
                    ? new[] { "update", project.Path }
                    : ["extract", project.Path, "--code-only", "--out", project.Path];
                var result = await RunToolAsync(graphify, arguments, cancellationToken);
                graphifySucceeded = result.Succeeded;
                if (result.Succeeded)
                    graphifyReady++;
                else
                    failures.Add($"{project.Name}: Graphify — {ShortError(result)}");
            }

            if (codebase is not null)
            {
                var result = await RunToolAsync(codebase,
                [
                    "cli", "index_repository", "--repo-path", project.Path,
                    "--mode", "moderate", "--persistence", "false"
                ], cancellationToken);
                if (result.Succeeded)
                    codebaseReady++;
                else
                    failures.Add($"{project.Name}: Codebase Memory — {ShortError(result)}");
            }

            if (graphifySucceeded && project.IsGit && gfy is not null)
            {
                var result = await RunToolAsync(gfy, ["sync"], cancellationToken, project.Path);
                if (!result.Succeeded)
                    failures.Add($"{project.Name}: gfy sync — {ShortError(result)}");
            }
        }

        if (gfy is not null && graphifyReady > 0)
        {
            var result = await RunToolAsync(gfy, ["sync-all"], cancellationToken);
            if (!result.Succeeded)
                failures.Add("Общий граф: gfy sync-all — " + ShortError(result));
        }

        var lines = new List<string>
        {
            $"Проектов обработано: {targets.Count}.",
            $"Graphify готов: {graphifyReady}/{targets.Count}.",
            $"Codebase Memory готов: {codebaseReady}/{targets.Count}.",
            gfy is null
                ? "gfy не найден: локальные графы созданы, общая регистрация пропущена."
                : "gfy найден: Git-проекты зарегистрированы, общий граф обновлён.",
            "В GitHub ничего не отправлялось. Codebase Memory создал только локальные машинные индексы."
        };
        lines.AddRange(failures.Take(12).Select(failure => "Ошибка: " + failure));
        if (failures.Count > 12)
            lines.Add($"Ещё ошибок: {failures.Count - 12}. Полные сведения есть в журнале приложения.");

        var summary = failures.Count == 0
            ? $"Индексы пересобраны для {targets.Count} проектов."
            : $"Пересборка индексов завершена частично: ошибок {failures.Count}.";
        return failures.Count == 0
            ? OperationResult.Ok(summary, string.Join(Environment.NewLine, lines))
            : OperationResult.Fail(summary, string.Join(Environment.NewLine, lines));
    }

    public async Task<OperationResult> RebindObsidianVaultsAsync(
        AppSettings settings,
        IEnumerable<ProjectEntry> projects,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.DestinationRoot) || !Directory.Exists(settings.DestinationRoot))
            return OperationResult.Fail("Сначала выберите существующую единую папку восстановленных проектов.");
        if (IsObsidianRunning())
            return OperationResult.Fail("Закройте Obsidian перед перепривязкой vault.");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var source = Path.Combine(profile, "CodexBridge-Recovery", "obsidian-vaults.json");
        if (!File.Exists(source))
            return OperationResult.Fail("Сохранённый реестр Obsidian не найден в папке восстановления.");

        var candidates = FindVaultCandidates(
            settings.DestinationRoot,
            GetIndexableProjects(projects).Select(project => project.Path),
            Math.Max(2, settings.ScanDepth),
            cancellationToken);
        var target = Path.Combine(appData, "obsidian", "obsidian.json");
        return await RebindObsidianRegistryAsync(
            source, target, settings.DestinationRoot, candidates, cancellationToken);
    }

    public static IReadOnlyList<ProjectEntry> GetIndexableProjects(IEnumerable<ProjectEntry> projects)
    {
        var result = new List<ProjectEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.Where(project => project.IsProtected && project.Status != ProjectStatus.Missing))
        {
            if (string.IsNullOrWhiteSpace(project.Path))
                continue;
            try
            {
                var path = Path.GetFullPath(project.Path);
                if (Directory.Exists(path) && paths.Add(path))
                    result.Add(project);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Некорректный путь просто не участвует в автоматической индексации.
            }
        }

        return result;
    }

    public static async Task<OperationResult> RebindObsidianRegistryAsync(
        string sourceRegistry,
        string targetRegistry,
        string destinationRoot,
        IEnumerable<string> vaultCandidates,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var destination = Path.GetFullPath(destinationRoot);
            var candidates = vaultCandidates
                .Select(Path.GetFullPath)
                .Where(path => Directory.Exists(path) && IsSameOrInside(path, destination))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceRoot = await ReadJsonObjectAsync(sourceRegistry, cancellationToken);
            if (sourceRoot["vaults"] is not JsonObject sourceVaults || sourceVaults.Count == 0)
                return OperationResult.Fail("Сохранённый реестр Obsidian не содержит vault.");

            JsonObject targetRoot;
            if (File.Exists(targetRegistry))
                targetRoot = await ReadJsonObjectAsync(targetRegistry, cancellationToken);
            else
                targetRoot = new JsonObject();
            JsonObject targetVaults;
            if (targetRoot["vaults"] is JsonObject existingVaults)
            {
                targetVaults = existingVaults;
            }
            else if (targetRoot["vaults"] is null)
            {
                targetVaults = new JsonObject();
                targetRoot["vaults"] = targetVaults;
            }
            else
            {
                return OperationResult.Fail("Текущий реестр Obsidian имеет неизвестный формат; изменения не применены.");
            }

            var mapped = 0;
            var updates = 0;
            var alreadyValid = 0;
            var unresolved = 0;
            var conflicts = 0;
            foreach (var vault in sourceVaults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (vault.Value is not JsonObject sourceEntry
                    || !TryGetString(sourceEntry["path"], out var oldPath))
                    continue;

                string? newPath = null;
                try
                {
                    var fullOldPath = Path.GetFullPath(oldPath);
                    if (Directory.Exists(fullOldPath) && IsSameOrInside(fullOldPath, destination))
                    {
                        newPath = fullOldPath;
                        alreadyValid++;
                    }
                    else
                    {
                        var leaf = Path.GetFileName(fullOldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        var matches = candidates.Where(candidate =>
                            string.Equals(Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                                leaf, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 1)
                            newPath = matches[0];
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Небезопасный или некорректный старый путь не применяется.
                }

                if (newPath is null)
                {
                    unresolved++;
                    continue;
                }

                var existingPath = targetVaults[vault.Key] is JsonObject existing
                    && TryGetString(existing["path"], out var configuredPath)
                    ? TryGetFullPath(configuredPath)
                    : null;
                if (existingPath is not null
                    && !string.Equals(existingPath, newPath, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(existingPath))
                {
                    conflicts++;
                    continue;
                }
                if (string.Equals(existingPath, newPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var replacement = (JsonObject)sourceEntry.DeepClone();
                replacement["path"] = newPath;
                targetVaults[vault.Key] = replacement;
                updates++;
                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    mapped++;
            }

            if (updates == 0)
            {
                var details = $"Уже доступны: {alreadyValid}; не найдены однозначно: {unresolved}; конфликты: {conflicts}.";
                return unresolved == 0 && conflicts == 0
                    ? OperationResult.Ok("Перепривязка Obsidian не требуется.", details)
                    : OperationResult.Fail("Не найдено ни одного однозначного нового пути vault.", details);
            }

            var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetRegistry))!;
            Directory.CreateDirectory(targetDirectory);
            var temporary = Path.Combine(targetDirectory, $"obsidian.codexbridge-{Guid.NewGuid():N}.tmp");
            var backup = Path.Combine(targetDirectory,
                $"obsidian.codexbridge-backup-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
            try
            {
                var json = targetRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken);
                if (File.Exists(targetRegistry))
                    File.Replace(temporary, targetRegistry, backup, ignoreMetadataErrors: true);
                else
                    File.Move(temporary, targetRegistry);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            var message = $"Obsidian: обновлено записей {updates}, изменено путей {mapped}.";
            var resultDetails = $"Не найдены однозначно: {unresolved}; конфликты сохранены: {conflicts}."
                                + (File.Exists(backup) ? Environment.NewLine + "Резервная копия реестра: " + backup : "");
            return OperationResult.Ok(message, resultDetails);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                           or ArgumentException or NotSupportedException)
        {
            return OperationResult.Fail("Не удалось безопасно перепривязать Obsidian vault.", exception.Message);
        }
    }

    private async Task<ProcessResult> RunToolAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var extension = Path.GetExtension(executable);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? await processes.RunCommandAsync(executable, arguments, cancellationToken, workingDirectory)
            : await processes.RunAsync(executable, arguments, cancellationToken: cancellationToken,
                workingDirectory: workingDirectory);
    }

    private static string ShortError(ProcessResult result)
    {
        var line = result.Combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? $"код завершения {result.ExitCode}" : line;
    }

    private static bool IsCodebaseMemoryName(string name) =>
        name.Contains("codebase", StringComparison.OrdinalIgnoreCase)
        || name.Contains("memory", StringComparison.OrdinalIgnoreCase);

    private static bool IsPonytailInstalled(string codexRoot) =>
        Directory.Exists(Path.Combine(codexRoot, "plugins", "cache", "ponytail"))
        || Directory.Exists(Path.Combine(codexRoot, "skills", "ponytail"));

    private static string? Find(IEnumerable<string> names, IEnumerable<string>? candidates = null) =>
        EnvironmentDiagnosticsService.FindExecutable(names, candidates);

    private static string? FindRestic(AppSettings settings) => Find(
        [settings.ResticExecutable, "restic.exe"],
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "restic.exe")]);

    private static string? FindRclone() => Find(["rclone.exe"],
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "rclone.exe")]);

    private static string? FindGit() => Find(["git.exe"],
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
    ]);

    private static string? FindNode() => Find(["node.exe"],
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")]);

    private static string? FindNpm(string appData) => Find(["npm.cmd"],
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"),
        Path.Combine(appData, "npm", "npm.cmd")
    ]);

    private static string? FindCodex(string localAppData, string appData) => Find(["codex.exe", "codex.cmd"],
    [
        Path.Combine(appData, "npm", "codex.cmd"),
        Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "codex.exe")
    ]);

    private static string? FindUv(string profile, string localAppData) => Find(["uv.exe"],
    [
        Path.Combine(profile, ".local", "bin", "uv.exe"),
        Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "uv.exe")
    ]);

    private static string? FindGraphify(string profile) => Find(["graphify.exe", "graphify.cmd", "graphify"],
        [Path.Combine(profile, ".local", "bin", "graphify.exe")]);

    private static string? FindCodebaseMemory(string profile, string appData) => Find(
        ["codebase-memory-mcp.exe", "codebase-memory-mcp.cmd"],
    [
        Path.Combine(profile, ".local", "bin", "codebase-memory-mcp.exe"),
        Path.Combine(appData, "npm", "codebase-memory-mcp.cmd")
    ]);

    private static string? FindGfy(string profile) => Find(["gfy.exe", "gfy.cmd", "gfy"],
        [Path.Combine(profile, ".local", "bin", "gfy.cmd")]);

    private static string? FindObsidian(string localAppData) => Find(["Obsidian.exe"],
        [Path.Combine(localAppData, "Programs", "Obsidian", "Obsidian.exe")]);

    private static bool IsObsidianRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("Obsidian");
            foreach (var process in processes)
                process.Dispose();
            return processes.Length > 0;
        }
        catch
        {
            return true;
        }
    }

    private static IReadOnlyList<string> FindVaultCandidates(
        string destinationRoot,
        IEnumerable<string> projectPaths,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationRoot);
        var roots = projectPaths
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return ""; }
            })
            .Where(path => Directory.Exists(path) && IsSameOrInside(path, destination))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!roots.Contains(destination, StringComparer.OrdinalIgnoreCase))
            roots.Add(destination);

        var vaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>(roots.Select(root => (root, 0)));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (!visited.Add(current.Path))
                continue;
            if (Directory.Exists(Path.Combine(current.Path, ".obsidian")))
                vaults.Add(current.Path);
            if (current.Depth >= maxDepth)
                continue;

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current.Path))
                {
                    var name = Path.GetFileName(directory);
                    if (!SkippedVaultDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                        pending.Push((directory, current.Depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Недоступная ветка не мешает поиску в остальных защищённых проектах.
            }
        }

        return vaults.ToList();
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? throw new JsonException("Ожидался JSON-объект.");
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = "";
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var candidate)
            || string.IsNullOrWhiteSpace(candidate))
            return false;
        value = candidate;
        return true;
    }

    private static string? TryGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrInside(string path, string root) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || PathPolicy.IsInside(path, root);
}
