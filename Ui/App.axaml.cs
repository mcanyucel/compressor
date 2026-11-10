using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ui.ViewModels;
using Ui.Views;

namespace Ui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            if (desktop.Args is [var argument, ..])
                Dispatcher.UIThread.Post(() => _ = viewModel.OpenAsync(argument));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
