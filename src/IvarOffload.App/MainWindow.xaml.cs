using System.ComponentModel;
using System.Windows;

namespace IvarOffload.App;

public partial class MainWindow : Window
{
    /// <summary>Below this work-area height (a 1366×768 laptop, or 1080p at 150 %) the window starts maximized.</summary>
    private const double ShortScreen = 800;

    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// Never start larger than the screen it opens on (the one under the mouse): on a short screen use all of it,
    /// otherwise shrink to fit and centre on it. The window is placed by hand rather than with CenterScreen, which lays
    /// it out at the primary monitor's scaling even on a monitor with another one.
    /// </summary>
    private void FitToWorkArea()
    {
        if (MonitorArea.UnderPointer() is not { } area)
        {
            Rect primary = SystemParameters.WorkArea;
            if (primary.Height < ShortScreen) WindowState = WindowState.Maximized;
            Width = Math.Min(Width, primary.Width - 32);
            Height = Math.Min(Height, primary.Height - 32);
            return;
        }
        Size units = area.SizeInUnits;
        Width = Math.Min(Width, units.Width - 32);
        Height = Math.Min(Height, units.Height - 32);
        // Window positions are in the units of the primary monitor's scaling until the window exists.
        double system = MonitorArea.SystemScale();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (area.Left + (area.Width - Width * area.Scale) / 2) / system;
        Top = (area.Top + (area.Height - Height * area.Scale) / 2) / system;
        if (units.Height < ShortScreen) WindowState = WindowState.Maximized;
    }

    /// <summary>"More below": scrolls the upper part down by what it shows, keeping a line of what was read for context.</summary>
    private void MoreBelow_Click(object sender, RoutedEventArgs e) =>
        UpperScroll.ScrollToVerticalOffset(UpperScroll.VerticalOffset + Math.Max(40, UpperScroll.ViewportHeight - 40));

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed || !ViewModel.IsAnyJobRunning)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (await ViewModel.TryStopForCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
