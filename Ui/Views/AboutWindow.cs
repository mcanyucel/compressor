using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bridgewiz.AppUpdate.Avalonia;
using Ui.Updates;

namespace Ui.Views;

/// <summary>
/// Help ▸ About: the version, the copyright, the bundled 7-Zip, the third-party licenses (THIRD-PARTY-NOTICES.txt,
/// 7-Zip's included: the LGPL asks that it ships with the app), Check for updates, and whether to check at startup. Closes with true when an update's installer has started.
/// </summary>
public sealed class AboutWindow : Window
{
    private static readonly string SevenZipFolder = Path.Combine(AppContext.BaseDirectory, "7zip");

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };

    public AboutWindow(UpdatePrompt updates, UserSettings settings)
    {
        Title = "About Compressor";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string copyright = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://CompressorGui/Assets/compressor-128.png"))),
            Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0),
        };
        DockPanel.SetDock(logo, Dock.Left);

        var startup = new CheckBox { Content = "Check for updates at startup", IsChecked = settings.CheckForUpdates, Margin = new Thickness(0, 10, 0, 0) };
        startup.IsCheckedChanged += (_, _) =>
        {
            if (startup.IsChecked is not { } on || on == settings.CheckForUpdates) return;
            settings.CheckForUpdates = on;
            settings.Save();
        };

        var license = new Button { Content = "Third-party licenses" };
        license.Click += (_, _) => OpenLicenses();

        var check = new Button { Content = "Check for updates", Margin = new Thickness(8, 0, 0, 0) };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            try
            {
                if (await updates.CheckNowAsync(this)) Close(true);
            }
            finally
            {
                check.IsEnabled = true;
            }
        };

        var close = new Button { Content = "Close", MinWidth = 90, IsDefault = true, IsCancel = true };
        close.Classes.Add("accent");
        close.Click += (_, _) => Close(false);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetColumn(check, 1);
        Grid.SetColumn(close, 3);
        buttons.Children.Add(license);
        buttons.Children.Add(check);
        buttons.Children.Add(close);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Width = 460,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        logo,
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = "Compressor", FontSize = 18, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = $"Version {UpdateVersion()}" },
                                new TextBlock { Text = copyright, Opacity = 0.7 },
                                new TextBlock { Text = "Open source under the MIT License.", Opacity = 0.7 },
                                new TextBlock
                                {
                                    Text = "Compresses the folders you choose into .7z archives next to them, and leaves the rest of the " +
                                           "folder tree as it is, so file names stay searchable.",
                                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                                },
                                new TextBlock
                                {
                                    Text = SevenZipNotice(),
                                    TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
                                },
                                startup,
                                _status,
                            },
                        },
                    },
                },
                buttons,
            },
        };
    }

    private static string UpdateVersion() =>
        $"{AppUpdates.Version.Major}.{AppUpdates.Version.Minor}.{Math.Max(AppUpdates.Version.Build, 0)}";

    private static string SevenZipNotice()
    {
        string version = File.Exists(Path.Combine(SevenZipFolder, "VERSION"))
            ? " " + File.ReadAllText(Path.Combine(SevenZipFolder, "VERSION")).Trim()
            : "";
        return $"Compression by 7-Zip{version}, copyright © Igor Pavlov, under the GNU LGPL (some parts BSD 3-clause). " +
               "It is bundled unmodified; its source is at 7-zip.org.";
    }

    /// <summary>
    /// Opens THIRD-PARTY-NOTICES.txt (all components, 7-Zip included) in the default text editor. A development build
    /// has none, so it falls back to 7-Zip's own License.txt.
    /// </summary>
    private void OpenLicenses()
    {
        string notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        string sevenZip = Path.Combine(SevenZipFolder, "License.txt");
        string? file = File.Exists(notices) ? notices : File.Exists(sevenZip) ? sevenZip : null;
        try
        {
            if (file is null) throw new FileNotFoundException("THIRD-PARTY-NOTICES.txt is not next to the app.");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception e) when (e is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            _status.Text = $"Could not open the licenses: {e.Message}";
            _status.Foreground = Brushes.Firebrick;
            _status.IsVisible = true;
        }
    }
}
