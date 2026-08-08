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
    private int _step;

    public SetupWizardWindow(AppSettings settings, SettingsStore settingsStore, DpapiSecretStore secrets)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _secrets = secrets;

        ProjectRootText.Text = settings.DestinationRoot;
        LocalRepositoryText.Text = settings.LocalRepository;
        CloudEnabledCheck.IsChecked = settings.CloudEnabled;
        CloudRepositoryText.Text = settings.CloudRepository;
        if (!secrets.Exists)
            GenerateKey();
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

    private void GenerateKey() =>
        RecoveryKeyText.Text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
            if (string.IsNullOrWhiteSpace(ProjectRootText.Text) || string.IsNullOrWhiteSpace(LocalRepositoryText.Text))
                throw new InvalidOperationException("Укажите папку проектов и локальное хранилище.");
            var projectRoot = Path.GetFullPath(ProjectRootText.Text.Trim());
            var localRepository = Path.GetFullPath(LocalRepositoryText.Text.Trim());
            if (PathPolicy.IsInside(localRepository, projectRoot))
                throw new InvalidOperationException("Хранилище резервной копии нельзя размещать внутри папки проектов.");
            if (!_secrets.Exists && string.IsNullOrWhiteSpace(RecoveryKeyText.Text))
                throw new InvalidOperationException("Создайте и сохраните ключ восстановления.");

            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(localRepository);
            if (!_settings.ProjectRoots.Contains(projectRoot, StringComparer.OrdinalIgnoreCase))
                _settings.ProjectRoots.Add(projectRoot);
            _settings.DestinationRoot = projectRoot;
            _settings.LocalRepository = localRepository;
            _settings.CloudRepository = CloudRepositoryText.Text.Trim();
            _settings.CloudEnabled = CloudEnabledCheck.IsChecked == true && _settings.CloudRepository.Length > 0;
            _settings.SetupCompleted = true;
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
        await _settingsStore.SaveAsync(_settings);
        DialogResult = false;
    }

    private void ShowStep(int step)
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
