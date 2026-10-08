using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IvarOffload.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // With virtual display adapters present (Parsec, virtual monitors) WPF's GPU path can fail to paint the first
        // frame, leaving a white window. This UI is light enough that software rendering costs nothing noticeable.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // File operations are journaled step by step, so an unexpected error never leaves a job in an unknown state.
        MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nAny job in progress can be resumed from its log.",
            WpfDialogs.Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
