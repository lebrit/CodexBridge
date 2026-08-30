using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBridge.Core;
using Microsoft.Win32;

namespace CodexBridge.App;

public partial class MainWindow : Window
{
    private readonly JsonFileStore _files = new();
    private readonly SettingsStore _settingsStore;
    private readonly CatalogStore _catalogStore;
    private readonly StateStore _stateStore;
    private readonly DpapiSecretStore _secrets = new();
    private readonly ProcessRunner _processes = new();
    private readonly ResticService _restic;
    private readonly ProjectDiscoveryService _discovery;
    private readonly SchedulerService _scheduler;
    private readonly BackupToolInstaller _toolInstaller;
    private readonly ToolInventoryService _toolInventory;
    private readonly RestoreService _restore;
    private readonly DispatcherTimer _stateRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ICollectionView? _projectsView;
    private AppSettings _settings = new();
    private DashboardAction _dashboardAction = DashboardAction.OpenSetup;
    private DateTime _stateLastWriteUtc;
    private bool _externalRefreshRunning;
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<ProjectEntry> Projects { get; } = [];
    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];
    public ObservableCollection<string> RecentActivities { get; } = [];

    private enum DashboardAction
    {
        InstallTools,
        OpenSetup,
        InitializeStorage,
        FindProjects,
        EnableScheduler,
        BackupNow
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _projectsView = CollectionViewSource.GetDefaultView(Projects);
        _projectsView.Filter = ProjectMatchesFilter;

        _settingsStore = new SettingsStore(_files);
        _catalogStore = new CatalogStore(_files);
        _stateStore = new StateStore(_files);
        _restic = new ResticService(_processes);
        _discovery = new ProjectDiscoveryService(_catalogStore);
        _scheduler = new SchedulerService(_processes);
        _toolInstaller = new BackupToolInstaller(_processes);
        _toolInventory = new ToolInventoryService(_processes);
        _restore = new RestoreService(_restic, _files);

        VersionText.Text = "Версия " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev");
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            _stateLastWriteUtc = GetStateLastWriteUtc();
            _stateRefreshTimer.Start();
        };
        _stateRefreshTimer.Tick += StateRefreshTimer_Tick;
        Closed += (_, _) => _stateRefreshTimer.Stop();
    }

    private async void StateRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_externalRefreshRunning || BusyPanel.Visibility == Visibility.Visible)
            return;

        var lastWriteUtc = GetStateLastWriteUtc();
        if (lastWriteUtc <= _stateLastWriteUtc)
            return;

        _externalRefreshRunning = true;
        try
        {
            _stateLastWriteUtc = lastWriteUtc;
            ReplaceProjects(await _catalogStore.LoadAsync());
            await RefreshDashboardAsync();
            AppendLog("Получен результат фонового запуска.");
        }
        catch (Exception exception)
        {
            ErrorLog.Write("Обновление состояния фонового агента", exception.Message, exception.ToString());
        }
        finally
        {
            _externalRefreshRunning = false;
        }
    }

    private static DateTime GetStateLastWriteUtc()
    {
        try
        {
            return File.Exists(AppPaths.StateFile)
                ? File.GetLastWriteTimeUtc(AppPaths.StateFile)
                : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private async Task LoadAsync()
    {
        await LoadSettingsAsync();
        if (!_settings.SetupCompleted)
            await ShowWizardAsync();
    }

    private Task LoadSettingsAsync() =>
        RunBusyAsync("Загрузка настроек…", async () =>
        {
            _settings = await _settingsStore.LoadAsync();
            ApplyTheme(_settings.Theme);
            ThemeButton.Content = _settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                ? "Тёмная тема"
                : "Светлая тема";
            Roots.Clear();
            foreach (var root in _settings.ProjectRoots)
                Roots.Add(root);

            LocalRepositoryText.Text = _settings.LocalRepository;
            CloudRepositoryText.Text = _settings.CloudRepository;
            CloudEnabledCheck.IsChecked = _settings.CloudEnabled;
            DestinationText.Text = _settings.DestinationRoot;
            RetentionEnabledCheck.IsChecked = _settings.RetentionEnabled;
            KeepDailyText.Text = _settings.KeepDaily.ToString();
            KeepWeeklyText.Text = _settings.KeepWeekly.ToString();
            KeepMonthlyText.Text = _settings.KeepMonthly.ToString();

            var vsCodeAvailable = ToolInventoryService.FindVsCodeExecutable() is not null;
            VsCodeCard.Visibility = vsCodeAvailable ? Visibility.Visible : Visibility.Collapsed;
            VsCodeIncludeCheck.IsChecked = vsCodeAvailable && _settings.IncludeVsCode;

            ReplaceProjects(await _catalogStore.LoadAsync());
            await RefreshDashboardAsync();
        });

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ContentTabs is null || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index))
            return;

        var pages = new[]
        {
            ("Обзор", "Состояние резервного копирования и быстрые действия"),
            ("Проекты", "Какие рабочие папки входят в защищённый каталог"),
            ("Восстановление", "Перенос снимка в единую папку на этом или новом компьютере"),
            ("Программы", "Список приложений и восстановление через WinGet"),
            ("Настройки", "Хранилища, корни проектов и автоматический запуск"),
            ("Журнал", "Результаты операций и диагностические сообщения")
        };
        if (index < 0 || index >= pages.Length)
            return;

        ContentTabs.SelectedIndex = index;
        PageTitle.Text = pages[index].Item1;
        PageSubtitle.Text = pages[index].Item2;
    }

    private async void OpenWizard_Click(object sender, RoutedEventArgs e) => await ShowWizardAsync();

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_dashboardAction)
        {
            case DashboardAction.InstallTools:
                InstallTools_Click(sender, e);
                break;
            case DashboardAction.OpenSetup:
                await ShowWizardAsync();
                break;
            case DashboardAction.InitializeStorage:
                await InitializeRepositoryAsync(cloud: false);
                break;
            case DashboardAction.FindProjects:
                RefreshProjects_Click(sender, e);
                break;
            case DashboardAction.EnableScheduler:
                InstallScheduler_Click(sender, e);
                break;
            case DashboardAction.BackupNow:
                BackupNow_Click(sender, e);
                break;
        }
    }

    private async Task ShowWizardAsync()
    {
        var wizard = new SetupWizardWindow(_settings, _settingsStore, _secrets) { Owner = this };
        wizard.ShowDialog();
        await LoadSettingsAsync();
    }

    private async void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var selectedPage = ContentTabs.SelectedIndex;
        _settings.Theme = _settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        ApplyTheme(_settings.Theme);
        new[] { OverviewNav, ProjectsNav, RestoreNav, ProgramsNav, SettingsNav, LogNav }[selectedPage].IsChecked = true;
        ThemeButton.Content = _settings.Theme == "Light" ? "Тёмная тема" : "Светлая тема";
        await _settingsStore.SaveAsync(_settings);
    }

    private static void ApplyTheme(string theme)
    {
        var light = theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
#pragma warning disable WPF0001
        Application.Current.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        var palette = light
            ? new[]
            {
                ("AppBackground", "#F1F5F9"), ("SidebarBackground", "#FFFFFF"),
                ("PanelBackground", "#FFFFFF"), ("InputBackground", "#F8FAFC"),
                ("PanelBorder", "#CBD5E1"), ("TextPrimary", "#0F172A"),
                ("TextMuted", "#526175"), ("ButtonBackground", "#E2E8F0"),
                ("ButtonHover", "#CBD5E1"), ("AlternatingBackground", "#F1F5F9"),
                ("NavHover", "#E2E8F0")
            }
            : new[]
            {
                ("AppBackground", "#0B1120"), ("SidebarBackground", "#111827"),
                ("PanelBackground", "#151F32"), ("InputBackground", "#0F172A"),
                ("PanelBorder", "#2A3953"), ("TextPrimary", "#F8FAFC"),
                ("TextMuted", "#9CA9BC"), ("ButtonBackground", "#1E293B"),
                ("ButtonHover", "#334155"), ("AlternatingBackground", "#151F32"),
                ("NavHover", "#1E293B")
            };

        foreach (var (key, value) in palette)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }

    private async void RefreshProjects_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Поиск проектов…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var result = await _discovery.RefreshAsync(_settings, cancellationToken);
            ReplaceProjects(result.Projects);
            AppendLog($"Просканировано каталогов: {result.ScannedDirectories}. Проектов: {result.Projects.Count}.");
            foreach (var warning in result.Warnings.Take(20))
                AppendLog("Предупреждение: " + warning);
            await RefreshDashboardAsync();
        });
    }

    private async void SaveProjects_Click(object sender, RoutedEventArgs e)
    {
        foreach (var project in Projects)
            project.Status = project.Status == ProjectStatus.Missing
                ? ProjectStatus.Missing
                : project.IsProtected ? ProjectStatus.Protected : ProjectStatus.Excluded;
        await _discovery.SaveAsync(Projects);
        _projectsView?.Refresh();
        UpdateProjectListSummary();
        AppendLog("Выбор защищаемых проектов сохранён.");
        await RefreshDashboardAsync();
    }

    private void ProjectFilter_Changed(object sender, RoutedEventArgs e)
    {
        _projectsView?.Refresh();
        UpdateProjectListSummary();
    }

    private bool ProjectMatchesFilter(object item)
    {
        if (item is not ProjectEntry project)
            return false;

        var filter = ProjectListFilter.All;
        if (ProjectStatusFilter?.SelectedItem is ComboBoxItem { Tag: string tag })
            Enum.TryParse(tag, out filter);
        return ProjectCatalogFilter.Matches(project, ProjectSearchText?.Text, filter);
    }

    private void ClearProjectFilter_Click(object sender, RoutedEventArgs e)
    {
        ProjectSearchText.Clear();
        ProjectStatusFilter.SelectedIndex = 0;
        _projectsView?.Refresh();
        UpdateProjectListSummary();
    }

    private void ProtectVisibleProjects_Click(object sender, RoutedEventArgs e) => SetVisibleProjectsProtection(true);

    private void ExcludeVisibleProjects_Click(object sender, RoutedEventArgs e) => SetVisibleProjectsProtection(false);

    private void SetVisibleProjectsProtection(bool protect)
    {
        var visibleProjects = _projectsView?.Cast<ProjectEntry>().ToList() ?? [];
        foreach (var project in visibleProjects)
        {
            project.IsProtected = protect;
            if (project.Status != ProjectStatus.Missing)
                project.Status = protect ? ProjectStatus.Protected : ProjectStatus.Excluded;
        }

        _projectsView?.Refresh();
        ProjectsGrid.Items.Refresh();
        UpdateProjectListSummary();
        AppendLog($"{(protect ? "Включена защита" : "Исключены")} показанных проектов: {visibleProjects.Count}. Сохраните выбор.");
    }

    private void OpenSelectedProject_Click(object sender, RoutedEventArgs e) => OpenSelectedProject();

    private void ProjectsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedProject();

    private void OpenSelectedProject()
    {
        if (ProjectsGrid.SelectedItem is not ProjectEntry project)
        {
            MessageBox.Show(this, "Выберите проект в списке.", "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Directory.Exists(project.Path))
        {
            MessageBox.Show(this, "Папка проекта сейчас недоступна.", "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = project.Path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось открыть папку", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsCoreAsync();
        SavePasswordIfEntered();
        AppendLog("Настройки сохранены локально.");
        await RefreshDashboardAsync();
    }

    private async void InitializeLocal_Click(object sender, RoutedEventArgs e) =>
        await InitializeRepositoryAsync(cloud: false);

    private async void InitializeCloud_Click(object sender, RoutedEventArgs e) =>
        await InitializeRepositoryAsync(cloud: true);

    private async Task InitializeRepositoryAsync(bool cloud)
    {
        await RunBusyAsync("Подключение хранилища…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            _secrets.Save(password);
            var repository = cloud ? _settings.CloudRepository : _settings.LocalRepository;
            await ShowResultAsync(await _restic.InitializeAsync(
                _settings.ResticExecutable, repository, password, cancellationToken));
            await RefreshDashboardAsync();
        });
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Создание резервной копии…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            SavePasswordIfEntered();
            var coordinator = new BackupCoordinator(_settingsStore, _catalogStore, _stateStore, _files, _secrets, _restic);
            var result = await coordinator.RunAsync(cancellationToken: cancellationToken);
            ReplaceProjects(await _catalogStore.LoadAsync(cancellationToken));
            await ShowResultAsync(result, recordActivity: false);
            await RefreshDashboardAsync();
        });
    }

    private async void CheckRepository_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Проверка хранилища…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var result = await _restic.CheckAsync(
                _settings.ResticExecutable, _settings.LocalRepository, password,
                cancellationToken: cancellationToken);
            await ShowResultAsync(result);
            if (result.Succeeded)
            {
                var state = await _stateStore.LoadAsync();
                state.LastCheckUtc = DateTimeOffset.UtcNow;
                await _stateStore.SaveAsync(state);
            }
            await RefreshDashboardAsync();
        });
    }

    private async void DeepCheckRepository_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(this,
            "Прочитать и проверить случайные 5% данных выбранного хранилища? Для облака это может занять время и использовать трафик.",
            "Глубокая проверка", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Глубокая проверка данных…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var result = await _restic.CheckAsync(
                _settings.ResticExecutable, SelectedRepository(), password,
                deep: true, cancellationToken: cancellationToken);
            await ShowResultAsync(result);
            if (result.Succeeded)
            {
                var state = await _stateStore.LoadAsync();
                state.LastCheckUtc = DateTimeOffset.UtcNow;
                await _stateStore.SaveAsync(state);
            }
            await RefreshDashboardAsync();
        });
    }

    private async void InstallScheduler_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsCoreAsync();
        var agent = Path.Combine(AppContext.BaseDirectory, "CodexBridge.Agent.exe");
        await ShowResultAsync(await _scheduler.InstallAsync(_settings.ScheduledTaskName, agent));
        await RefreshDashboardAsync();
    }

    private async void RemoveScheduler_Click(object sender, RoutedEventArgs e)
    {
        await ShowResultAsync(await _scheduler.RemoveAsync(_settings.ScheduledTaskName));
        await RefreshDashboardAsync();
    }

    private async void RunScheduledBackup_Click(object sender, RoutedEventArgs e)
    {
        await ShowResultAsync(await _scheduler.RunNowAsync(_settings.ScheduledTaskName), recordActivity: false);
        await RefreshDashboardAsync();
    }

    private async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Установка restic и rclone…", async cancellationToken =>
        {
            await ShowResultAsync(await _toolInstaller.InstallAsync(cancellationToken));
            await RefreshDashboardAsync();
        });
    }

    private async void CaptureApps_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Обновление списка программ…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            await ShowResultAsync(await _toolInventory.CaptureAsync(_settings.IncludeVsCode, cancellationToken));
        });
    }

    private async void InstallApps_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(this,
            _settings.IncludeVsCode
                ? "Установить доступные приложения и расширения VS Code из восстановленных списков? Для некоторых установщиков может потребоваться UAC."
                : "Установить доступные приложения из восстановленного WinGet-списка? Для некоторых установщиков может потребоваться UAC.",
            "Установка программ", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Установка приложений…", async cancellationToken =>
            await ShowResultAsync(await _toolInventory.InstallAppsAsync(_settings.IncludeVsCode, cancellationToken)));
    }

    private void ConfigureRclone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rclone.exe", "config") { UseShellExecute = true });
            AppendLog("Открыт интерактивный мастер rclone. После настройки нажмите «Найти настроенные remotes».");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Сначала установите rclone.\n\n" + exception.Message,
                "rclone не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DetectRcloneRemotes_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Поиск rclone remotes…", async cancellationToken =>
        {
            var result = await _processes.RunAsync(
                "rclone.exe", ["listremotes"], cancellationToken: cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException("Не удалось прочитать настройки rclone.\n" + result.Combined);

            var remotes = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.TrimEnd(':')).Where(value => value.Length > 0).ToList();
            if (remotes.Count == 0)
                throw new InvalidOperationException("Настроенные rclone remotes не найдены.");

            RcloneRemoteNameText.Text = remotes[0];
            AppendLog("Найдены rclone remotes: " + string.Join(", ", remotes));
            UseRcloneRemote();
        });
    }

    private void UseRcloneRemote_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UseRcloneRemote();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "rclone", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UseRcloneRemote()
    {
        var name = RcloneRemoteNameText.Text.Trim().TrimEnd(':');
        if (name.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase))
            name = name[7..].TrimEnd(':');
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Укажите имя rclone remote.");
        if (name.Contains(':'))
            throw new InvalidOperationException("Укажите только имя remote без пути и дополнительных двоеточий.");

        CloudRepositoryText.Text = $"rclone:{name}:CodexBridge/restic-v1";
        CloudEnabledCheck.IsChecked = true;
    }

    private async void LoadSnapshots_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Загрузка снимков…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var snapshots = await _restic.SnapshotsAsync(
                _settings.ResticExecutable, SelectedRepository(), password, cancellationToken);
            Snapshots.Clear();
            foreach (var snapshot in snapshots)
                Snapshots.Add(snapshot);
            if (snapshots.Count > 0)
                SnapshotsList.SelectedIndex = 0;
            AppendLog($"Найдено снимков: {snapshots.Count}.");
        });
    }

    private async void VerifyRestore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotInfo snapshot)
        {
            MessageBox.Show(this, "Выберите снимок.", "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync("Проверка снимка и расчёт изменений…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var result = await _restore.PlanRestoreAsync(
                _settings, SelectedRepository(), password, snapshot.Id, DestinationText.Text, cancellationToken);
            var state = await _stateStore.LoadAsync(cancellationToken);
            state.RecordRestoreTest(result.Succeeded, result.Message, snapshotId: snapshot.Id);
            await _stateStore.SaveAsync(state, cancellationToken);
            await ShowResultAsync(result, recordActivity: false);
            await RefreshDashboardAsync();
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotInfo snapshot)
        {
            MessageBox.Show(this, "Выберите снимок.", "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var state = await _stateStore.LoadAsync();
        var planIsCurrent = state.LastRestoreTestSucceeded
            && string.Equals(state.LastRestoreTestSnapshotId, snapshot.Id, StringComparison.OrdinalIgnoreCase);
        var planStatus = planIsCurrent
            ? "Dry-run этого снимка успешно выполнен."
            : "ВНИМАНИЕ: для выбранного снимка нет успешного актуального dry-run.";
        var confirmation = MessageBox.Show(this,
            $"Восстановить снимок {snapshot.Id} в {DestinationText.Text}?\n\n{planStatus}\nСуществующие отличающиеся файлы не будут перезаписаны.",
            "Подтверждение восстановления", MessageBoxButton.YesNo,
            planIsCurrent ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Восстановление снимка…", async cancellationToken =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            await ShowResultAsync(await _restore.RestoreSnapshotAsync(
                _settings, SelectedRepository(), password, snapshot.Id, DestinationText.Text, cancellationToken));
        });
    }

    private async void SnapshotsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RefreshDashboardAsync();

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите корень проектов", Multiselect = false };
        if (dialog.ShowDialog(this) == true && !Roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            Roots.Add(dialog.FolderName);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string selected)
            Roots.Remove(selected);
    }

    private void ChooseLocalRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите пустую папку локального backup", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            LocalRepositoryText.Text = dialog.FolderName;
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите единую папку для проектов", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            DestinationText.Text = dialog.FolderName;
    }

    private async Task SaveSettingsCoreAsync()
    {
        _settings.ProjectRoots = Roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _settings.LocalRepository = LocalRepositoryText.Text.Trim();
        _settings.CloudRepository = CloudRepositoryText.Text.Trim();
        _settings.CloudEnabled = CloudEnabledCheck.IsChecked == true;
        _settings.DestinationRoot = DestinationText.Text.Trim();
        if (VsCodeCard.Visibility == Visibility.Visible)
            _settings.IncludeVsCode = VsCodeIncludeCheck.IsChecked == true;
        _settings.RetentionEnabled = RetentionEnabledCheck.IsChecked == true;
        _settings.KeepDaily = ParseRetention(KeepDailyText, "Дни", 365);
        _settings.KeepWeekly = ParseRetention(KeepWeeklyText, "Недели", 104);
        _settings.KeepMonthly = ParseRetention(KeepMonthlyText, "Месяцы", 120);
        await _settingsStore.SaveAsync(_settings);
    }

    private static int ParseRetention(TextBox input, string name, int maximum)
    {
        if (!int.TryParse(input.Text, out var value) || value < 1 || value > maximum)
            throw new InvalidOperationException($"Поле «{name}» должно содержать число от 1 до {maximum}.");
        return value;
    }

    private string SelectedRepository() => CloudSourceRadio.IsChecked == true
        ? _settings.CloudRepository
        : _settings.LocalRepository;

    private string? GetPassword() => !string.IsNullOrWhiteSpace(PasswordInput.Password)
        ? PasswordInput.Password
        : _secrets.Load();

    private void SavePasswordIfEntered()
    {
        if (!string.IsNullOrWhiteSpace(PasswordInput.Password))
            _secrets.Save(PasswordInput.Password);
    }

    private void ReplaceProjects(IEnumerable<ProjectEntry> projects)
    {
        Projects.Clear();
        foreach (var project in projects)
            Projects.Add(project);
        _projectsView?.Refresh();
        UpdateProjectListSummary();
    }

    private void UpdateProjectListSummary()
    {
        if (ProjectListSummaryText is null)
            return;

        var visibleCount = _projectsView?.Cast<object>().Count() ?? Projects.Count;
        var protectedCount = Projects.Count(project => project.IsProtected && project.Status != ProjectStatus.Missing);
        ProjectListSummaryText.Text = $"Показано {visibleCount} из {Projects.Count} · защищено {protectedCount}";
    }

    private async Task RefreshDashboardAsync()
    {
        var state = await _stateStore.LoadAsync();
        var protectedCount = Projects.Count(project => project.IsProtected && project.Status != ProjectStatus.Missing);
        var missingCount = Projects.Count(project => project.IsProtected && project.Status == ProjectStatus.Missing);
        ProjectCountText.Text = $"{protectedCount} защищено";
        ProjectDetailsText.Text = missingCount > 0
            ? $"{missingCount} недоступно · {Projects.Count} всего"
            : $"Все доступны · {Projects.Count} всего";

        LastBackupText.Text = FormatRelativeTime(state.LastLocalBackupUtc, "Ещё не создавалась");
        LocalBackupDetailsText.Text = state.LastLocalBackupUtc is null
            ? "Первая копия будет зашифрована"
            : "Последняя успешная: " + state.LastLocalBackupUtc.Value.ToLocalTime().ToString("g");
        CloudBackupText.Text = !_settings.CloudEnabled
            ? "Выключена"
            : FormatRelativeTime(state.LastCloudBackupUtc, "Ещё не создавалась");
        CloudBackupDetailsText.Text = !_settings.CloudEnabled
            ? "Можно включить в настройках"
            : state.LastCloudBackupUtc is null
                ? "Ожидает первой загрузки"
                : "Последняя успешная: " + state.LastCloudBackupUtc.Value.ToLocalTime().ToString("g");
        LastMessageText.Text = string.IsNullOrWhiteSpace(state.LastMessage)
            ? "История появится после первой операции."
            : state.LastMessage;

        var selectedSnapshot = SnapshotsList.SelectedItem as SnapshotInfo;
        var selectedSnapshotWasTested = selectedSnapshot is not null
            && string.Equals(state.LastRestoreTestSnapshotId, selectedSnapshot.Id, StringComparison.OrdinalIgnoreCase);
        if (state.LastRestoreTestUtc is null || selectedSnapshot is not null && !selectedSnapshotWasTested)
        {
            RestoreCheckTitleText.Text = state.LastRestoreTestUtc is null
                ? "Проверка ещё не выполнялась"
                : "Выбранный снимок ещё не проверен";
            RestoreCheckStatusText.Text = "Выберите папку проектов, затем запустите проверку и расчёт изменений.";
            RestoreCheckSymbol.Text = "?";
            RestoreCheckIcon.Background = (Brush)Application.Current.Resources["ButtonBackground"];
        }
        else
        {
            RestoreCheckTitleText.Text = state.LastRestoreTestSucceeded
                ? "Снимок проверен, изменения рассчитаны"
                : "Последняя проверка не пройдена";
            RestoreCheckStatusText.Text = $"{FormatRelativeTime(state.LastRestoreTestUtc, "")} · {state.LastRestoreTestMessage}";
            RestoreCheckSymbol.Text = state.LastRestoreTestSucceeded ? "✓" : "!";
            RestoreCheckIcon.Background = (Brush)Application.Current.Resources[
                state.LastRestoreTestSucceeded ? "StatusGood" : "StatusDanger"];
        }

        var backupFailed = state.LastRunUtc is not null && !state.LastRunSucceeded;
        var restoreTestFailed = state.LastRestoreTestUtc is not null && !state.LastRestoreTestSucceeded;
        AttentionCard.Visibility = backupFailed || restoreTestFailed ? Visibility.Visible : Visibility.Collapsed;
        AttentionText.Text = backupFailed && restoreTestFailed
            ? "Последний backup и проверка восстановления завершились ошибкой. Откройте Обзор и Восстановление."
            : backupFailed
                ? "Последний backup завершился ошибкой. Откройте Обзор."
                : "Проверка восстановления завершилась ошибкой. Откройте Восстановление.";

        RecentActivities.Clear();
        state.RecentActivities ??= [];
        foreach (var activity in state.RecentActivities.Take(6))
        {
            var marker = activity.Succeeded ? "✓" : "!";
            RecentActivities.Add($"{marker}  {activity.RecordedUtc.ToLocalTime():g} — {activity.Message}");
        }

        var agentExecutable = Path.Combine(AppContext.BaseDirectory, "CodexBridge.Agent.exe");
        var taskStatus = await _scheduler.GetStatusAsync(_settings.ScheduledTaskName, agentExecutable);
        var resticAvailable = (await _restic.VersionAsync(_settings.ResticExecutable)).Succeeded;
        var lastRun = state.LastRunUtc is null
            ? "запусков ещё не было"
            : $"последний запуск {FormatRelativeTime(state.LastRunUtc, "")}, " +
              (state.LastRunSource == BackupRunSource.Automatic ? "автоматически" : "вручную");
        AutomationStatusText.Text = !taskStatus.Installed
            ? "Автокопирование выключено"
            : taskStatus.UsesCurrentAgent
                ? $"Автокопирование: каждый час · {lastRun}"
                : "Автокопирование требует обновления после смены версии программы";

        if (!resticAvailable)
            SetDashboardStatus("Нужны инструменты резервного копирования",
                "Установите restic и rclone, после этого CodexBridge сможет создавать зашифрованные снимки.",
                "Установить инструменты", DashboardAction.InstallTools, "StatusWarning", "!");
        else if (!_secrets.Exists)
            SetDashboardStatus("Сохраните ключ восстановления",
                "Без ключа невозможно создать или открыть зашифрованную копию.",
                "Открыть мастер", DashboardAction.OpenSetup, "StatusWarning", "!");
        else if (string.IsNullOrWhiteSpace(_settings.LocalRepository))
            SetDashboardStatus("Выберите локальное хранилище",
                "Укажите отдельную папку, в которой будут храниться зашифрованные снимки.",
                "Открыть мастер", DashboardAction.OpenSetup, "StatusWarning", "!");
        else if (!Directory.Exists(_settings.LocalRepository))
            SetDashboardStatus("Подготовьте локальное хранилище",
                "Выбранная папка ещё не создана или сейчас недоступна.",
                "Подключить хранилище", DashboardAction.InitializeStorage, "StatusWarning", "!");
        else if (protectedCount == 0)
            SetDashboardStatus("Найдите проекты для защиты",
                "CodexBridge пока не видит ни одного доступного защищаемого проекта.",
                "Найти проекты", DashboardAction.FindProjects, "StatusWarning", "!");
        else if (missingCount > 0)
            SetDashboardStatus("Некоторые проекты недоступны",
                "Проверьте подключённые диски или обновите список проектов.",
                "Обновить список", DashboardAction.FindProjects, "StatusWarning", "!");
        else if (state.LastLocalBackupUtc is null)
            SetDashboardStatus("Всё готово к первой копии",
                "Настройки заполнены. Создайте первый зашифрованный снимок проектов.",
                "Создать первую копию", DashboardAction.BackupNow, "StatusWarning", "!");
        else if (!state.LastRunSucceeded)
            SetDashboardStatus("Резервная копия требует внимания",
                state.LastMessage,
                "Повторить копирование", DashboardAction.BackupNow, "StatusDanger", "!");
        else if (!taskStatus.Installed || !taskStatus.UsesCurrentAgent)
            SetDashboardStatus(taskStatus.Installed ? "Обновите автоматическое расписание" : "Проекты защищены вручную",
                taskStatus.Installed
                    ? "Расписание указывает на предыдущую папку программы. Обновите его одним нажатием."
                    : "Последняя копия успешна, но автоматическое расписание пока выключено.",
                taskStatus.Installed ? "Обновить автобэкап" : "Включить автобэкап",
                DashboardAction.EnableScheduler, "StatusWarning", "!");
        else
            SetDashboardStatus("Всё защищено",
                "Последняя копия успешна, проекты доступны, автоматическое расписание работает.",
                "Создать копию сейчас", DashboardAction.BackupNow, "StatusGood", "✓");

        _stateLastWriteUtc = GetStateLastWriteUtc();
    }

    private void SetDashboardStatus(
        string title,
        string description,
        string actionText,
        DashboardAction action,
        string colorResource,
        string symbol)
    {
        ReadinessText.Text = title;
        StatusDescriptionText.Text = description;
        PrimaryActionButton.Content = actionText;
        _dashboardAction = action;
        StatusSymbol.Text = symbol;
        var brush = (Brush)Application.Current.Resources[colorResource];
        ProtectionStatusCard.BorderBrush = brush;
        StatusIcon.Background = brush;
    }

    private static string FormatRelativeTime(DateTimeOffset? value, string emptyText)
    {
        if (value is null)
            return emptyText;

        var elapsed = DateTimeOffset.UtcNow - value.Value;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "Только что";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} мин назад";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)elapsed.TotalHours)} ч назад";
        if (elapsed < TimeSpan.FromDays(7))
            return $"{Math.Max(1, (int)elapsed.TotalDays)} дн назад";
        return value.Value.ToLocalTime().ToString("d");
    }

    private Task RunBusyAsync(string message, Func<Task> action) =>
        RunBusyAsync(message, _ => action(), canCancel: false);

    private Task RunBusyAsync(string message, Func<CancellationToken, Task> action) =>
        RunBusyAsync(message, action, canCancel: true);

    private async Task RunBusyAsync(
        string message,
        Func<CancellationToken, Task> action,
        bool canCancel)
    {
        if (BusyPanel.Visibility == Visibility.Visible)
            return;

        MainContent.IsEnabled = false;
        Sidebar.IsEnabled = false;
        BusyText.Text = message;
        BusyElapsedText.Text = "";
        BusyPanel.Visibility = Visibility.Visible;
        BusyProgress.IsIndeterminate = true;
        CancelBusyButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelBusyButton.IsEnabled = canCancel;
        _operationCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) => BusyElapsedText.Text = $"{stopwatch.Elapsed:mm\\:ss}";
        elapsedTimer.Start();
        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            const string cancelled = "Операция отменена пользователем.";
            AppendLog(cancelled);
            AddActivityToDashboard(true, cancelled);
            var state = await _stateStore.LoadAsync();
            state.RecordActivity(true, cancelled);
            await _stateStore.SaveAsync(state);
        }
        catch (Exception exception)
        {
            AppendLog("Ошибка: " + exception.Message);
            AddActivityToDashboard(false, exception.Message);
            try
            {
                var state = await _stateStore.LoadAsync();
                state.RecordActivity(false, exception.Message);
                await _stateStore.SaveAsync(state);
            }
            catch
            {
                // Основная ошибка уже попадёт в файловый журнал ниже.
            }
            var logPath = ErrorLog.Write("Интерфейс", exception.Message, exception.ToString());
            var logHint = string.IsNullOrWhiteSpace(logPath) ? "" : $"\n\nПодробности записаны в:\n{logPath}";
            MessageBox.Show(this, exception.Message + logHint, "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            elapsedTimer.Stop();
            stopwatch.Stop();
            MainContent.IsEnabled = true;
            Sidebar.IsEnabled = true;
            BusyProgress.IsIndeterminate = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            BusyText.Text = "";
            BusyElapsedText.Text = "";
            CancelBusyButton.Visibility = Visibility.Collapsed;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void CancelBusy_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested)
            return;

        CancelBusyButton.IsEnabled = false;
        BusyText.Text = "Безопасно останавливаем операцию…";
        _operationCancellation.Cancel();
    }

    private async Task ShowResultAsync(OperationResult result, bool recordActivity = true)
    {
        AppendLog(result.Message);
        AddActivityToDashboard(result.Succeeded, result.Message);
        if (!string.IsNullOrWhiteSpace(result.Details))
            AppendLog(result.Details);
        if (recordActivity)
        {
            var state = await _stateStore.LoadAsync();
            state.RecordActivity(result.Succeeded, result.Message);
            await _stateStore.SaveAsync(state);
        }
        if (!result.Succeeded)
        {
            var logPath = ErrorLog.Write("Операция CodexBridge", result.Message, result.Details);
            var logHint = string.IsNullOrWhiteSpace(logPath) ? "" : $"\n\nПодробности записаны в:\n{logPath}";
            MessageBox.Show(this, result.Message + logHint, "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddActivityToDashboard(bool succeeded, string message)
    {
        LastMessageText.Text = message;
        RecentActivities.Insert(0, $"{(succeeded ? "✓" : "!")}  {DateTimeOffset.Now:g} — {message}");
        while (RecentActivities.Count > 6)
            RecentActivities.RemoveAt(RecentActivities.Count - 1);
    }

    private void AppendLog(string message)
    {
        LogText.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        LogText.ScrollToEnd();
    }
}
