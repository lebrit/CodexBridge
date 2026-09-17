using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBridge.Core;

namespace CodexBridge.App;

public partial class MainWindow
{
    // Exercises production XAML with synthetic data; no profile, tools or backup operations.
    internal async Task RunSmokeTestAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        LocalRepositoryText.Text = @"C:\Demo Backups\restic-v1";
        DestinationText.Text = @"C:\Demo Projects";
        ReadinessText.Text = "Тестовый режим";
        Roots.Add(@"C:\Demo Projects");
        Projects.Add(new ProjectEntry { Name = "Демо-проект", Path = @"C:\Demo Projects\demo", IsProtected = true });
        var checks = new List<string>();
        var navigation = new[] { OverviewNav, ProjectsNav, RestoreNav, ProgramsNav, SettingsNav, LogNav };
        foreach (var theme in new[] { "Dark", "Light" })
        {
            ApplyTheme(theme);
            foreach (var size in new[] { (Width: 1220d, Height: 780d), (Width: MinWidth, Height: MinHeight) })
            {
                Width = size.Width;
                Height = size.Height;
                for (var page = 0; page < navigation.Length; page++)
                {
                    navigation[page].IsChecked = true;
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                    if (ContentTabs.SelectedIndex != page || !navigation[page].IsVisible)
                        throw new InvalidOperationException($"Navigation failed: {theme}, page {page}");
                    AssertInputContrast(LocalRepositoryText);
                    SaveSmokeImage(this, Path.Combine(reportDirectory, $"{theme}-{size.Width}-page-{page}.png"));
                    checks.Add($"{theme}/{size.Width}/page-{page}: OK");
                }
            }

            var wizard = new SetupWizardWindow(new AppSettings
            {
                DestinationRoot = @"C:\Demo Projects", LocalRepository = @"C:\Demo Backups\restic-v1"
            }, _settingsStore, _secrets, previewOnly: true)
            {
                Owner = this, ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false
            };
            wizard.Show();
            try
            {
                wizard.Width = wizard.MinWidth;
                wizard.Height = wizard.MinHeight;
                for (var step = 0; step < 3; step++)
                {
                    wizard.ShowStep(step);
                    await Dispatcher.InvokeAsync(wizard.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var back = (Button)wizard.FindName("BackButton");
                    var next = (Button)wizard.FindName("NextButton");
                    if (!next.IsVisible || (step > 0 && (!back.IsVisible || Bounds(back, wizard).IntersectsWith(Bounds(next, wizard)))))
                        throw new InvalidOperationException("Wizard navigation buttons overlap or are hidden.");
                    AssertInputContrast((TextBox)wizard.FindName("LocalRepositoryText"));
                    SaveSmokeImage(wizard, Path.Combine(reportDirectory, $"{theme}-wizard-{step}.png"));
                    checks.Add($"{theme}/wizard-{step}: OK");
                }
            }
            finally { wizard.Close(); }
        }
        await File.WriteAllLinesAsync(Path.Combine(reportDirectory, "success.txt"), checks);
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void AssertInputContrast(Control input)
    {
        if (input.Foreground is not SolidColorBrush foreground || input.Background is not SolidColorBrush background)
            throw new InvalidOperationException("Input colors are not explicit solid brushes.");
        static double Luminance(Color color)
        {
            static double Linear(byte channel) => channel / 255d <= 0.04045
                ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(foreground.Color);
        var b = Luminance(background.Color);
        if ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) < 4.5)
            throw new InvalidOperationException("Input text contrast is below 4.5:1.");
    }

    private static void SaveSmokeImage(Window window, string path)
    {
        // Render the client area including its background and content margins, not the native title bar.
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
