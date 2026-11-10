using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Bridgewiz.AppUpdate.Avalonia;
using Ui.Updates;
using Ui.ViewModels;

namespace Ui.Views;

/// <summary>
/// The file and folder pickers, Help (updates, About) and the startup update check live here; everything else is in
/// <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType JobFileType = new("Compressor job") { Patterns = ["*.json"] };

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await CheckForUpdatesOnStartupAsync();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>The user's preferences (update checks), in %AppData%\Compressor\settings.json.</summary>
    public UserSettings Settings { get; init; } = UserSettings.Load(UserSettings.DefaultStore);

    // ── Updates and About ────────────────────────────────────────────────────────────

    private UpdatePrompt CreateUpdatePrompt() => AppUpdates.Prompt(Settings, ConfirmUpdateAsync);

    /// <summary>
    /// Looks for a newer version once the window is up. Quiet when there's none or the server can't be reached; only
    /// an update the user hasn't skipped brings up a dialog. Release builds only, so that development builds don't
    /// keep offering the released version.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
#if DEBUG
        await Task.CompletedTask;
#else
        if (await CreateUpdatePrompt().CheckOnStartupAsync(this)) Close();
#endif
    }

    /// <summary>
    /// Before the installer starts, which closes the app. A running job would be stopped halfway, so the update waits
    /// for it; a check in progress is simply cancelled.
    /// </summary>
    private Task<bool> ConfirmUpdateAsync()
    {
        if (ViewModel.IsRunning)
        {
            ViewModel.ErrorMessage = "Update postponed: a job is running, and installing the update closes the app. " +
                                     "Let the job finish (or stop it), then use Help ▸ Check for updates.";
            return Task.FromResult(false);
        }

        if (ViewModel.IsChecking && ViewModel.CancelCheckCommand.CanExecute(null))
            ViewModel.CancelCheckCommand.Execute(null);
        return Task.FromResult(true);
    }

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (await CreateUpdatePrompt().CheckNowAsync(this)) Close();
    });

    private async void OnAbout(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var about = new AboutWindow(CreateUpdatePrompt(), Settings) { Icon = Icon };
        if (await about.ShowDialog<bool>(this)) Close();
    });

    // ── Pickers ──────────────────────────────────────────────────────────────────────

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder to archive",
            AllowMultiple = false
        });

        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
            await ViewModel.OpenFolderAsync(path);
    });

    private async void OnLoadJob(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a job file",
            AllowMultiple = false,
            FileTypeFilter = [JobFileType]
        });

        if (files is [var file] && file.TryGetLocalPath() is { } path)
            await ViewModel.LoadJobAsync(path);
    });

    private async void OnSaveJob(object? sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the job file",
            SuggestedFileName = "job.json",
            DefaultExtension = "json",
            FileTypeChoices = [JobFileType]
        });

        if (file?.TryGetLocalPath() is { } path)
            ViewModel.SaveJob(path);
    });

    /// <summary>
    /// Closing while running stops the job: cancelling kills the running 7-Zip processes. Any partial archive is left
    /// as .7z.tmp and reported as a leftover by the next check.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ViewModel.IsRunning && ViewModel.StopCommand.CanExecute(null))
            ViewModel.StopCommand.Execute(null);
        base.OnClosing(e);
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ViewModel.ErrorMessage = ex.Message; } // async void handlers must not throw
    }
}
