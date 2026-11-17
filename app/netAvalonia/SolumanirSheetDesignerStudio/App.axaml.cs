using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.App.Views;
using SolumanirSheetDesignerStudio.Core.Settings;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App;

public partial class App : Application
{
    /// <summary>The shell view model, reachable from the crash handlers in <see cref="Program"/>.</summary>
    public static MainViewModel? Main { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            Loc.Load(settings.Language);
            // New rows, slots and pages get their default names in the user's language.
            Core.Model.Factory.Translate = Loc.T;
            var main = Main = new MainViewModel(settings, new RenderServices());
            Dispatcher.UIThread.UnhandledException += OnUiThreadException;
            string? fileArgument = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-'));
            // Default shutdown mode (last window closed): see MainWindow.OnClosing for why the main-window mode is not used.
            desktop.MainWindow = new MainWindow(main, fileArgument);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static long _errorWindowStart;
    private static int _errorsInWindow;

    /// <summary>
    /// A bug in one command should cost the user that command, not the document: unsaved work is written to the
    /// recovery folder, the error is shown, and the app keeps running. Errors arriving in a burst are a loop, though;
    /// those are allowed to end the process (the recovery data is already on disk).
    /// </summary>
    private static void OnUiThreadException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Main?.EmergencySnapshot();
        Console.Error.WriteLine(e.Exception);
        long now = Environment.TickCount64;
        if (now - _errorWindowStart > 10_000)
        {
            _errorWindowStart = now;
            _errorsInWindow = 0;
        }
        if (++_errorsInWindow > 5) return;
        Main?.Toasts.Error(Loc.T("Something went wrong: {0}", e.Exception.Message));
        e.Handled = true;
    }
}
