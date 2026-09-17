using System.IO;
using System.Security.Cryptography;
using System.Windows;
using CodexBridge.Core;
using Microsoft.Win32;

namespace CodexBridge.App;

public partial class SetupWizardWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DpapiSecretStore _secrets;
    private readonly bool _previewOnly;
    private bool _initialized;
    private string? _generatedKey;
    private int _step;

    public SetupWizardWindow(AppSettings settings, SettingsStore settingsStore, DpapiSecretStore secrets, bool previewOnly = false)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _secrets = secrets;
        _previewOnly = previewOnly;

        ProjectRootText.Text = settings.DestinationRoot;
        LocalRepositoryText.Text = settings.LocalRepository;
        CloudEnabledCheck.IsChecked = settings.CloudEnabled;
        CloudRepositoryText.Text = settings.CloudRepository;
        if (settings.PendingNewComputerRestore)
            NewComputerModeRadio.IsChecked = true;
        if (previewOnly)
            RecoveryKeyText.Text = "DEMO — ключ в тестовом режиме не создаётся";
        else if (!settings.PendingNewComputerRestore && !secrets.Exists)
            GenerateKey();
        _initialized = true;
        ApplySetupMode();
    }

    private void BrowseProjectRoot_Click(object sender, RoutedEventArgs e) =>
        ChooseFolder(ProjectRootText, "Выберите единую папку проектов");

    private void BrowseRepository_Click(object sender, RoutedEventArgs e) =>
        ChooseFolder(LocalRepositoryText, "Выберите папку локальной резервной копии");

    private void ChooseFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e) => GenerateKey();

    private void GenerateKey()
    {
        _generatedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecoveryKeyText.Text = _generatedKey;
    }

    private void SetupMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            ApplySetupMode();
    }

    private void ApplySetupMode()
    {
        var recovery = NewComputerModeRadio.IsChecked == true;
        WizardIntroText.Text = recovery
            ? "Укажите существующее хранилище и ваш сохранённый ключ. Сначала CodexBridge проверит снимок и покажет план; рабочие файлы без подтверждения не меняются."
            : "Мастер подготовит приложение так, чтобы новые проекты и восстановленная среда находились в одном понятном месте.";
        WizardPlanText.Text = recovery
            ? "• единая папка для восстановленных проектов\n• подключение существующей локальной или облачной копии\n• проверка ключа только при чтении снимков\n• безопасный dry-run перед восстановлением"
            : "• единая папка проектов\n• локальное зашифрованное хранилище\n• ключ восстановления, защищённый Windows DPAPI\n• при желании — вторая копия через rclone в вашем облаке";
        BackupPageTitle.Text = recovery ? "Подключение существующей копии" : "Защита резервной копии";
        LocalRepositoryLabel.Text = recovery
            ? "Существующее локальное хранилище (если оно доступно)"
            : "Локальное хранилище";
        RecoveryKeyLabel.Text = recovery ? "Сохранённый ключ восстановления" : "Ключ восстановления";
        RecoveryKeyHelp.Text = recovery
            ? "Введите ключ, который был сохранён отдельно на старом компьютере. CodexBridge не может получить его из облака."
            : "Сохраните ключ отдельно. Без него резервную копию нельзя восстановить на другом компьютере.";
        CloudRepositoryLabel.Text = recovery
            ? "Адрес существующего restic/rclone хранилища"
            : "Адрес restic/rclone (можно заполнить позже)";
        CloudEnabledCheck.Content = recovery
            ? "Использовать существующее облачное хранилище"
            : "Добавить вторую копию в моё облако";
        GenerateKeyButton.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;

        if (recovery && !_previewOnly && _generatedKey is not null
            && string.Equals(RecoveryKeyText.Text, _generatedKey, StringComparison.Ordinal))
            RecoveryKeyText.Clear();
        else if (!recovery && !_previewOnly && !_secrets.Exists && string.IsNullOrWhiteSpace(RecoveryKeyText.Text))
            GenerateKey();
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RecoveryKeyText.Text))
            Clipboard.SetText(RecoveryKeyText.Text);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            ShowStep(_step - 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 2)
        {
            ShowStep(_step + 1);
            return;
        }

        try
        {
            var recovery = NewComputerModeRadio.IsChecked == true;
            var cloudRepository = CloudRepositoryText.Text.Trim();
            var cloudEnabled = CloudEnabledCheck.IsChecked == true && cloudRepository.Length > 0;
            if (string.IsNullOrWhiteSpace(ProjectRootText.Text))
                throw new InvalidOperationException("Укажите единую папку проектов.");
            if (!recovery && string.IsNullOrWhiteSpace(LocalRepositoryText.Text))
                throw new InvalidOperationException("Укажите локальное хранилище.");
            if (recovery && string.IsNullOrWhiteSpace(LocalRepositoryText.Text) && !cloudEnabled)
                throw new InvalidOperationException("Укажите существующее локальное или облачное хранилище.");
            var projectRoot = Path.GetFullPath(ProjectRootText.Text.Trim());
            var localRepository = string.IsNullOrWhiteSpace(LocalRepositoryText.Text)
                ? ""
                : Path.GetFullPath(LocalRepositoryText.Text.Trim());
            if (localRepository.Length > 0 && PathPolicy.IsInside(localRepository, projectRoot))
                throw new InvalidOperationException("Хранилище резервной копии нельзя размещать внутри папки проектов.");
            if (recovery && !cloudEnabled
                && (!Directory.Exists(localRepository) || !File.Exists(Path.Combine(localRepository, "config"))))
                throw new InvalidOperationException("Выбранная локальная папка не содержит существующий restic-репозиторий.");
            if (!_secrets.Exists && string.IsNullOrWhiteSpace(RecoveryKeyText.Text))
                throw new InvalidOperationException(recovery
                    ? "Введите сохранённый ключ восстановления."
                    : "Создайте и сохраните ключ восстановления.");

            Directory.CreateDirectory(projectRoot);
            if (!recovery && localRepository.Length > 0)
                Directory.CreateDirectory(localRepository);
            if (!_settings.ProjectRoots.Contains(projectRoot, StringComparer.OrdinalIgnoreCase))
                _settings.ProjectRoots.Add(projectRoot);
            _settings.DestinationRoot = projectRoot;
            _settings.LocalRepository = localRepository;
            _settings.CloudRepository = cloudRepository;
            _settings.CloudEnabled = cloudEnabled;
            _settings.SetupCompleted = true;
            _settings.PendingNewComputerRestore = recovery;
            if (!string.IsNullOrWhiteSpace(RecoveryKeyText.Text))
                _secrets.Save(RecoveryKeyText.Text.Trim());
            await _settingsStore.SaveAsync(_settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось завершить настройку", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetupCompleted = true;
        _settings.PendingNewComputerRestore = false;
        await _settingsStore.SaveAsync(_settings);
        DialogResult = false;
    }

    internal void ShowStep(int step)
    {
        _step = step;
        WelcomePage.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsPage.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = $"Шаг {step + 1} из 3";
        BackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = step == 2 ? "Завершить" : "Далее";
    }
}
