using System.Reflection;

namespace CodexBridge.Core;

public sealed class BackupCoordinator(
    SettingsStore settingsStore,
    CatalogStore catalogStore,
    StateStore stateStore,
    JsonFileStore files,
    DpapiSecretStore secrets,
    ResticService restic)
{
    public async Task<OperationResult> RunAsync(
        BackupRunSource runSource = BackupRunSource.Manual,
        CancellationToken cancellationToken = default)
    {
        FileStream backupLock;
        try
        {
            AppPaths.EnsureCreated();
            backupLock = new FileStream(AppPaths.BackupLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return OperationResult.Fail("Резервное копирование уже выполняется.");
        }

        using (backupLock)
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var configuration = ValidateStorageSettings(settings);
            if (!configuration.Succeeded)
                return await FinishAsync(false, configuration.Message, runSource, cancellationToken);
            var discovery = await new ProjectDiscoveryService(catalogStore).RefreshAsync(settings, cancellationToken);
            var projects = discovery.Projects;
            var password = secrets.Load();
            if (string.IsNullOrWhiteSpace(password))
                return await FinishAsync(false, "Сначала сохраните ключ резервной копии.", runSource, cancellationToken);
            var protectedProjects = projects
                .Where(project => project.IsProtected && project.Status != ProjectStatus.Missing && Directory.Exists(project.Path))
                .ToList();
            if (protectedProjects.Count == 0)
                return await FinishAsync(false, "Не найдено защищённых проектов.", runSource, cancellationToken);

            var includeVsCode = settings.IncludeVsCode && ToolInventoryService.FindVsCodeExecutable() is not null;
            var capture = await new ToolInventoryService(new ProcessRunner()).CaptureAsync(includeVsCode, cancellationToken);
            var environment = BackupFiles.DiscoverEnvironment(includeVsCode);
            var manifest = new BackupManifest
            {
                ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                MachineName = Environment.MachineName,
                CreatedUtc = DateTimeOffset.UtcNow,
                Projects = protectedProjects.Select(project => new ManifestProject
                {
                    Id = project.Id,
                    Name = project.Name,
                    SourcePath = project.Path,
                    IsGit = project.IsGit
                }).ToList(),
                Environment = environment,
                ExcludedForSafety = BackupFiles.ExcludedForSafety.ToList(),
                RequiresManualAction = BackupFiles.RequiresManualAction.ToList()
            };

            await files.SaveAsync(AppPaths.BackupManifestFile, manifest, cancellationToken);
            await BackupFiles.EnsureExcludesAsync(cancellationToken);

            var projectPaths = PathPolicy.ReduceNestedRoots(protectedProjects.Select(project => project.Path));
            if (projectPaths.Any(path => PathPolicy.IsInside(settings.LocalRepository, path)))
                return await FinishAsync(false, "Локальный backup находится внутри защищаемого проекта. Выберите другой каталог.", runSource, cancellationToken);

            var localConfig = Path.Combine(Path.GetFullPath(settings.LocalRepository), "config");
            if (!File.Exists(localConfig))
            {
                var initialization = await restic.InitializeAsync(
                    settings.ResticExecutable, settings.LocalRepository, password, cancellationToken);
                if (!initialization.Succeeded)
                    return await FinishAsync(false, initialization.Message, runSource, cancellationToken, initialization.Details);
            }

            var sources = projectPaths
                .Concat(environment.Select(item => item.SourcePath))
                .Append(AppPaths.SettingsFile)
                .Append(AppPaths.CatalogFile)
                .Append(AppPaths.BackupManifestFile)
                .ToList();

            var local = await restic.BackupAsync(settings.ResticExecutable, settings.LocalRepository, password, sources, cancellationToken);
            if (!local.Succeeded)
                return await FinishAsync(false, local.Message, runSource, cancellationToken, local.Details);

            var state = await stateStore.LoadAsync(cancellationToken);
            state.LastLocalBackupUtc = DateTimeOffset.UtcNow;
            state.LastRunUtc = DateTimeOffset.UtcNow;
            state.LastRunSource = runSource;
            state.LastRunSucceeded = false;
            state.LastMessage = "Локальная копия готова; оставшиеся этапы ещё не завершены.";
            // Keep evidence of the completed local snapshot even if cloud work is cancelled or crashes.
            await stateStore.SaveAsync(state, CancellationToken.None);
            var cloudBackedUp = false;

            if (settings.CloudEnabled)
            {
                var cloud = await restic.BackupAsync(settings.ResticExecutable, settings.CloudRepository, password, sources, cancellationToken);
                if (!cloud.Succeeded)
                {
                    state.RecordRun(false, "Локальная копия готова, облачная ожидает повтора: " + cloud.Message, runSource);
                    await stateStore.SaveAsync(state, cancellationToken);
                    return OperationResult.Fail(state.LastMessage, cloud.Details);
                }

                state.LastCloudBackupUtc = DateTimeOffset.UtcNow;
                state.LastMessage = "Локальная и облачная копии готовы; итоговая проверка ещё не завершена.";
                await stateStore.SaveAsync(state, CancellationToken.None);
                cloudBackedUp = true;
            }

            if (settings.RetentionEnabled && capture.Succeeded)
            {
                var targets = new List<(string Name, string Repository)> { ("локальное", settings.LocalRepository) };
                if (cloudBackedUp)
                    targets.Add(("облачное", settings.CloudRepository));

                var failures = new List<string>();
                foreach (var target in targets)
                {
                    var retention = await restic.ApplyRetentionAsync(
                        settings.ResticExecutable, target.Repository, password,
                        settings.KeepDaily, settings.KeepWeekly, settings.KeepMonthly, cancellationToken);
                    if (!retention.Succeeded)
                        failures.Add($"{target.Name}: {retention.Message}");
                }

                if (failures.Count > 0)
                {
                    state.RecordRun(false, "Новые копии готовы, но очистка старых снимков требует внимания.", runSource);
                    await stateStore.SaveAsync(state, cancellationToken);
                    return OperationResult.Fail(state.LastMessage, string.Join(Environment.NewLine, failures));
                }
            }

            var completion = DescribeCompletedBackup(cloudBackedUp, capture, local.Details);
            state.RecordRun(completion.Succeeded, completion.Message, runSource);
            await stateStore.SaveAsync(state, cancellationToken);
            return completion;
        }
    }

    public static OperationResult ValidateStorageSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalRepository))
            return OperationResult.Fail("Не настроен локальный репозиторий.");
        if (settings.CloudEnabled && string.IsNullOrWhiteSpace(settings.CloudRepository))
            return OperationResult.Fail("Облачная копия включена, но адрес не указан. Укажите адрес или отключите облачную копию.");
        return OperationResult.Ok("Хранилища настроены.");
    }

    public static OperationResult DescribeCompletedBackup(bool cloudBackedUp, OperationResult capture, string localDetails)
    {
        var message = cloudBackedUp ? "Локальная и облачная копии готовы." : "Локальная копия готова.";
        return capture.Succeeded
            ? OperationResult.Ok(message, localDetails)
            : OperationResult.Fail(message + " Профиль среды сохранён не полностью; требуется внимание.",
                string.Join(Environment.NewLine, localDetails, capture.Message, capture.Details));
    }

    private async Task<OperationResult> FinishAsync(
        bool success,
        string message,
        BackupRunSource runSource,
        CancellationToken cancellationToken,
        string details = "")
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        state.RecordRun(success, message, runSource);
        await stateStore.SaveAsync(state, cancellationToken);
        return success ? OperationResult.Ok(message, details) : OperationResult.Fail(message, details);
    }
}
