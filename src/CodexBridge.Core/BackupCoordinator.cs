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
    public async Task<OperationResult> RunAsync(CancellationToken cancellationToken = default)
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
            var projects = await catalogStore.LoadAsync(cancellationToken);
            var password = secrets.Load();
            if (string.IsNullOrWhiteSpace(password))
                return await FinishAsync(false, "Сначала сохраните ключ резервной копии.", cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.LocalRepository))
                return await FinishAsync(false, "Не настроен локальный репозиторий.", cancellationToken);

            var protectedProjects = projects
                .Where(project => project.IsProtected && project.Status != ProjectStatus.Missing && Directory.Exists(project.Path))
                .ToList();
            if (protectedProjects.Count == 0)
                return await FinishAsync(false, "Не найдено защищённых проектов.", cancellationToken);

            var includeVsCode = settings.IncludeVsCode && ToolInventoryService.FindVsCodeExecutable() is not null;
            await new ToolInventoryService(new ProcessRunner()).CaptureAsync(includeVsCode, cancellationToken);
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
                Environment = environment
            };

            await files.SaveAsync(AppPaths.BackupManifestFile, manifest, cancellationToken);
            await BackupFiles.EnsureExcludesAsync(cancellationToken);

            var projectPaths = PathPolicy.ReduceNestedRoots(protectedProjects.Select(project => project.Path));
            if (projectPaths.Any(path => PathPolicy.IsInside(settings.LocalRepository, path)))
                return await FinishAsync(false, "Локальный backup находится внутри защищаемого проекта. Выберите другой каталог.", cancellationToken);

            var localConfig = Path.Combine(Path.GetFullPath(settings.LocalRepository), "config");
            if (!File.Exists(localConfig))
            {
                var initialization = await restic.InitializeAsync(
                    settings.ResticExecutable, settings.LocalRepository, password, cancellationToken);
                if (!initialization.Succeeded)
                    return await FinishAsync(false, initialization.Message, cancellationToken, initialization.Details);
            }

            var sources = projectPaths
                .Concat(environment.Select(item => item.SourcePath))
                .Append(AppPaths.SettingsFile)
                .Append(AppPaths.CatalogFile)
                .Append(AppPaths.BackupManifestFile)
                .ToList();

            var local = await restic.BackupAsync(settings.ResticExecutable, settings.LocalRepository, password, sources, cancellationToken);
            if (!local.Succeeded)
                return await FinishAsync(false, local.Message, cancellationToken, local.Details);

            var state = await stateStore.LoadAsync(cancellationToken);
            state.LastLocalBackupUtc = DateTimeOffset.UtcNow;
            var cloudBackedUp = false;

            if (settings.CloudEnabled && !string.IsNullOrWhiteSpace(settings.CloudRepository))
            {
                var cloud = await restic.BackupAsync(settings.ResticExecutable, settings.CloudRepository, password, sources, cancellationToken);
                if (!cloud.Succeeded)
                {
                    state.LastRunSucceeded = false;
                    state.LastMessage = "Локальная копия готова, облачная ожидает повтора: " + cloud.Message;
                    await stateStore.SaveAsync(state, cancellationToken);
                    return OperationResult.Fail(state.LastMessage, cloud.Details);
                }

                state.LastCloudBackupUtc = DateTimeOffset.UtcNow;
                cloudBackedUp = true;
            }

            if (settings.RetentionEnabled)
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
                    state.LastRunSucceeded = false;
                    state.LastMessage = "Новые копии готовы, но очистка старых снимков требует внимания.";
                    await stateStore.SaveAsync(state, cancellationToken);
                    return OperationResult.Fail(state.LastMessage, string.Join(Environment.NewLine, failures));
                }
            }

            state.LastRunSucceeded = true;
            state.LastMessage = settings.CloudEnabled ? "Локальная и облачная копии готовы." : "Локальная копия готова.";
            await stateStore.SaveAsync(state, cancellationToken);
            return OperationResult.Ok(state.LastMessage, local.Details);
        }
    }

    private async Task<OperationResult> FinishAsync(
        bool success,
        string message,
        CancellationToken cancellationToken,
        string details = "")
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        state.LastRunSucceeded = success;
        state.LastMessage = message;
        await stateStore.SaveAsync(state, cancellationToken);
        return success ? OperationResult.Ok(message, details) : OperationResult.Fail(message, details);
    }
}
