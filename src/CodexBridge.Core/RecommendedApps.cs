namespace CodexBridge.Core;

public sealed record RecommendedApp(
    string PackageId,
    string Name,
    string Description,
    string Source,
    bool SelectedByDefault = false);

public sealed record RecommendedAppsPlan(
    IReadOnlyList<RecommendedApp> Selected,
    IReadOnlyList<RecommendedApp> Installed,
    IReadOnlyList<RecommendedApp> Pending,
    bool InventoryKnown,
    bool WingetAvailable,
    string Warning);

/// <summary>Small, fixed allowlist for preparing a new Windows computer before restoring a backup.</summary>
public sealed class RecommendedAppsService(ProcessRunner processes, ToolInventoryService inventory)
{
    private const string PythonManagerId = "Python.PythonInstallManager";

    public static IReadOnlyList<RecommendedApp> Catalog { get; } =
    [
        new("9PLM9XGG6VKS", "ChatGPT для Windows", "Официальное приложение OpenAI; вход в аккаунт выполняется вручную.", "msstore", true),
        new("Git.Git", "Git", "Работа с репозиториями и функциями Git в Codex.", "winget", true),
        new(PythonManagerId, "Python", "Менеджер Python и актуальная стабильная версия интерпретатора.", "winget", true),
        new("OpenJS.NodeJS.LTS", "Node.js LTS + npm", "npm входит в Node.js; нужен многим проектам и Codex CLI.", "winget", true),
        new("GitHub.cli", "GitHub CLI", "Команды gh для GitHub; вход в аккаунт выполняется вручную.", "winget"),
        new("Microsoft.DotNet.SDK.10", ".NET SDK 10", "Сборка приложений .NET и Windows-проектов.", "winget"),
        new("Microsoft.VisualStudioCode", "VS Code", "Редактор кода; CodexBridge и ChatGPT могут работать без него.", "winget")
    ];

    public static RecommendedAppsPlan BuildPlan(
        IEnumerable<string> selectedPackageIds,
        IEnumerable<string> installedPackageIds,
        bool inventoryKnown = true,
        bool wingetAvailable = true,
        string warning = "")
    {
        var selectedIds = selectedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = selectedIds.Except(Catalog.Select(app => app.PackageId), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException("Список содержит приложение вне разрешённого каталога.", nameof(selectedPackageIds));

        var selected = Catalog.Where(app => selectedIds.Contains(app.PackageId)).ToList();
        var installedIds = installedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = inventoryKnown
            ? selected.Where(app => installedIds.Contains(app.PackageId)).ToList()
            : [];
        var pending = selected.Except(installed).ToList();
        return new RecommendedAppsPlan(selected, installed, pending, inventoryKnown, wingetAvailable, warning);
    }

    public async Task<RecommendedAppsPlan> PreviewAsync(
        IEnumerable<string> selectedPackageIds,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedPackageIds.ToArray();
        // Validate the request before probing the computer or invoking WinGet.
        _ = BuildPlan(selected, []);
        var winget = await processes.RunAsync("winget.exe", ["--version"], cancellationToken: cancellationToken);
        if (!winget.Succeeded)
            return BuildPlan(selected, [], false, false,
                "WinGet недоступен. Установите или обновите App Installer из Microsoft Store.");

        try
        {
            var current = await inventory.CaptureInstalledWingetPackageIdsAsync(cancellationToken);
            return BuildPlan(selected, current.PackageIds, current.InventoryKnown, true, current.Warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BuildPlan(selected, [], false, true,
                "Список установленных программ недоступен. WinGet пропустит уже установленные версии (--no-upgrade).");
        }
    }

    public static string[] BuildWingetInstallArguments(RecommendedApp app)
    {
        if (!Catalog.Contains(app))
            throw new ArgumentException("Приложение отсутствует в разрешённом каталоге.", nameof(app));
        return
        [
            "install", "--id", app.PackageId, "--exact", "--source", app.Source,
            "--no-upgrade", "--accept-package-agreements", "--accept-source-agreements",
            "--disable-interactivity"
        ];
    }

    public async Task<OperationResult> InstallAsync(
        IEnumerable<string> selectedPackageIds,
        CancellationToken cancellationToken = default)
    {
        var plan = await PreviewAsync(selectedPackageIds.ToArray(), cancellationToken);
        if (plan.Selected.Count == 0)
            return OperationResult.Fail("Сначала выберите хотя бы одну программу.");
        if (!plan.WingetAvailable)
            return OperationResult.Fail("WinGet не найден; установка программ невозможна.", plan.Warning);

        var outcomes = new List<string>();
        var failed = false;
        var pythonManagerReady = plan.Installed.Any(app => app.PackageId == PythonManagerId);
        foreach (var app in plan.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await processes.RunAsync("winget.exe",
                BuildWingetInstallArguments(app), cancellationToken: cancellationToken);
            if (!result.Succeeded)
                failed = true;
            if (app.PackageId == PythonManagerId)
                pythonManagerReady = result.Succeeded;
            outcomes.Add($"{app.Name}: {(result.Succeeded ? "установлено" : "ошибка установки")}."
                + (result.Succeeded ? "" : " " + result.Combined));
        }

        // WinGet installs the manager, not the Python runtime. The manager resolves
        // "default" to the current stable Python release at the time of installation.
        if (pythonManagerReady)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var python = await processes.RunAsync(FindPythonManagerExecutable(),
                ["install", "-y", "default"], cancellationToken: cancellationToken);
            if (!python.Succeeded)
                failed = true;
            outcomes.Add(python.Succeeded
                ? "Python: актуальный стабильный интерпретатор установлен или уже имеется."
                : "Python: не удалось установить интерпретатор. " + python.Combined);
        }

        var alreadyInstalled = plan.Installed
            .Where(app => app.PackageId != PythonManagerId)
            .Select(app => app.Name + ": уже установлено; пропущено.");
        var details = string.Join(Environment.NewLine, alreadyInstalled.Concat(outcomes));
        return failed
            ? OperationResult.Fail("Часть выбранных программ установить не удалось.", details)
            : OperationResult.Ok("Выбранные программы обработаны.", details);
    }

    private static string FindPythonManagerExecutable()
    {
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        var candidates = new[]
        {
            Path.Combine(windowsApps, "pymanager.exe"),
            Path.Combine(windowsApps, "PythonSoftwareFoundation.PythonManager_qbz5n2kfra8p0", "pymanager.exe"),
            Path.Combine(windowsApps, "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km", "pymanager.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "pymanager.exe";
    }
}
