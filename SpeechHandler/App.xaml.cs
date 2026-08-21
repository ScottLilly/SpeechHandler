using System.Windows;
using System.Windows.Threading;

namespace SpeechHandler;

public partial class App : Application
{
    internal static bool IsExiting { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (IsExiting)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                e.Exception.Message,
                "Speech Handler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Avoid throwing from the handler, especially while a window is closing.
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                ex.Message,
                "Speech Handler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // The process is already failing; avoid throwing from the handler.
        }
    }
}
