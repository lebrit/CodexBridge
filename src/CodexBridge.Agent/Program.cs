using CodexBridge.Core;

try
{
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
