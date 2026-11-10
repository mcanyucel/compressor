using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ui.ViewModels;

namespace Ui.Views;

/// <summary>
/// Only the file and folder pickers live here; everything else is in <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType JobFileType = new("Compressor job") { Patterns = ["*.json"] };

    public MainWindow() => InitializeComponent();

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

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
