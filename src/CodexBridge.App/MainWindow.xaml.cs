using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private AppSettings _settings = new();
    private DashboardAction _dashboardAction = DashboardAction.OpenSetup;

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
        Loaded += async (_, _) => await LoadAsync();
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
        await RunBusyAsync("Поиск проектов…", async () =>
        {
            await SaveSettingsCoreAsync();
            var result = await _discovery.RefreshAsync(_settings);
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
        AppendLog("Выбор защищаемых проектов сохранён.");
        await RefreshDashboardAsync();
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
        await RunBusyAsync("Подключение хранилища…", async () =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            _secrets.Save(password);
            var repository = cloud ? _settings.CloudRepository : _settings.LocalRepository;
            await ShowResultAsync(await _restic.InitializeAsync(_settings.ResticExecutable, repository, password));
            await RefreshDashboardAsync();
        });
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Создание резервной копии…", async () =>
        {
            await SaveSettingsCoreAsync();
            SavePasswordIfEntered();
            var discovery = await _discovery.RefreshAsync(_settings);
            ReplaceProjects(discovery.Projects);
            var coordinator = new BackupCoordinator(_settingsStore, _catalogStore, _stateStore, _files, _secrets, _restic);
            await ShowResultAsync(await coordinator.RunAsync(), recordActivity: false);
            await RefreshDashboardAsync();
        });
    }

    private async void CheckRepository_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Проверка хранилища…", async () =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var result = await _restic.CheckAsync(_settings.ResticExecutable, _settings.LocalRepository, password);
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

        await RunBusyAsync("Глубокая проверка данных…", async () =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var result = await _restic.CheckAsync(_settings.ResticExecutable, SelectedRepository(), password, deep: true);
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

    private async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Установка restic и rclone…", async () =>
        {
            await ShowResultAsync(await _toolInstaller.InstallAsync());
            await RefreshDashboardAsync();
        });
    }

    private async void CaptureApps_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Обновление списка программ…", async () =>
        {
            await SaveSettingsCoreAsync();
            await ShowResultAsync(await _toolInventory.CaptureAsync(_settings.IncludeVsCode));
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

        await RunBusyAsync("Установка приложений…", async () =>
            await ShowResultAsync(await _toolInventory.InstallAppsAsync(_settings.IncludeVsCode)));
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
        await RunBusyAsync("Поиск rclone remotes…", async () =>
        {
            var result = await _processes.RunAsync("rclone.exe", ["listremotes"]);
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
        await RunBusyAsync("Загрузка снимков…", async () =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            var snapshots = await _restic.SnapshotsAsync(_settings.ResticExecutable, SelectedRepository(), password);
            Snapshots.Clear();
            foreach (var snapshot in snapshots)
                Snapshots.Add(snapshot);
            AppendLog($"Найдено снимков: {snapshots.Count}.");
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotInfo snapshot)
        {
            MessageBox.Show(this, "Выберите снимок.", "CodexBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"Восстановить снимок {snapshot.Id} в {DestinationText.Text}?\n\nСуществующие отличающиеся файлы не будут перезаписаны.",
            "Подтверждение восстановления", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Восстановление снимка…", async () =>
        {
            await SaveSettingsCoreAsync();
            var password = GetPassword() ?? throw new InvalidOperationException("Введите ключ восстановления.");
            await ShowResultAsync(await _restore.RestoreSnapshotAsync(
                _settings, SelectedRepository(), password, snapshot.Id, DestinationText.Text));
        });
    }

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

        RecentActivities.Clear();
        state.RecentActivities ??= [];
        foreach (var activity in state.RecentActivities.Take(6))
        {
            var marker = activity.Succeeded ? "✓" : "!";
            RecentActivities.Add($"{marker}  {activity.RecordedUtc.ToLocalTime():g} — {activity.Message}");
        }

        var taskInstalled = await _scheduler.ExistsAsync(_settings.ScheduledTaskName);
        var resticAvailable = (await _restic.VersionAsync(_settings.ResticExecutable)).Succeeded;
        AutomationStatusText.Text = taskInstalled
            ? "Автокопирование: каждый час"
            : "Автокопирование выключено";

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
        else if (!taskInstalled)
            SetDashboardStatus("Проекты защищены вручную",
                "Последняя копия успешна, но автоматическое расписание пока выключено.",
                "Включить автобэкап", DashboardAction.EnableScheduler, "StatusWarning", "!");
        else
            SetDashboardStatus("Всё защищено",
                "Последняя копия успешна, проекты доступны, автоматическое расписание работает.",
                "Создать копию сейчас", DashboardAction.BackupNow, "StatusGood", "✓");
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

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        if (BusyPanel.Visibility == Visibility.Visible)
            return;

        MainContent.IsEnabled = false;
        Sidebar.IsEnabled = false;
        BusyText.Text = message;
        BusyPanel.Visibility = Visibility.Visible;
        BusyProgress.IsIndeterminate = true;
        try
        {
            await action();
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
            MainContent.IsEnabled = true;
            Sidebar.IsEnabled = true;
            BusyProgress.IsIndeterminate = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            BusyText.Text = "";
        }
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
