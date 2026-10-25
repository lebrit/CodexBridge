using CodexBridge.Core;

try
{
    if (args.Length == 1 && args[0] == "--uninstall-scheduler")
    {
        var uninstallFiles = new JsonFileStore();
        var uninstallSettings = await uninstallFiles.LoadAsync(AppPaths.SettingsFile, () => new AppSettings());
        var scheduler = new SchedulerService(new ProcessRunner());
        var agent = Path.Combine(AppContext.BaseDirectory, "CodexBridge.Agent.exe");
        foreach (var taskName in new[] { new AppSettings().ScheduledTaskName, uninstallSettings.ScheduledTaskName }.Distinct())
        {
            var removed = await scheduler.RemoveOwnedAsync(taskName, agent);
            if (!removed.Succeeded)
            {
                ErrorLog.Write("Удаление расписания", removed.Message, removed.Details);
                return 1;
            }
        }
        return 0;
    }
    if (args.Length != 0)
        return 2;
    AppPaths.EnsureCreated();

    var files = new JsonFileStore();
    var settingsStore = new SettingsStore(files);
    var catalogStore = new CatalogStore(files);
    var stateStore = new StateStore(files);
    var processRunner = new ProcessRunner();
    var restic = new ResticService(processRunner);

    var coordinator = new BackupCoordinator(
        settingsStore,
        catalogStore,
        stateStore,
        files,
        new DpapiSecretStore(),
        restic);

    var result = await coordinator.RunAsync(BackupRunSource.Automatic);
    Console.WriteLine(result.Message);
    if (!string.IsNullOrWhiteSpace(result.Details))
        Console.WriteLine(result.Details);
    if (!result.Succeeded)
        ErrorLog.Write("Фоновый агент", result.Message, result.Details);

    return result.Succeeded ? 0 : 1;
}
catch (Exception exception)
{
    ErrorLog.Write("Необработанная ошибка фонового агента", exception.Message, exception.ToString());
    Console.Error.WriteLine(exception.Message);
    return 1;
}
