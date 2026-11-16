using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IvarOffload.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // With virtual display adapters present (Parsec, virtual monitors) WPF's GPU path can fail to paint the first
        // frame, leaving a white window. This UI is light enough that software rendering costs nothing noticeable.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DispatcherUnhandledException += OnUnhandled;
        UseIvarFont();
        base.OnStartup(e);
        ApplyIvarTheme();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Dispatcher.BeginInvoke(ApplyIvarTheme);
        };
    }

    // IVAR design system: Montserrat for all text. It is in the exe, so the PC needs no font installed.
    private static readonly FontFamily IvarFont = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Montserrat");

    private static void UseIvarFont()
    {
        // Windows, popups and tooltips are the roots that text inherits its font from.
        var font = new FrameworkPropertyMetadata(IvarFont);
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(Window), font);
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(ToolTip), new FrameworkPropertyMetadata(IvarFont));
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(ContextMenu), new FrameworkPropertyMetadata(IvarFont));
        TextBlock.FontFamilyProperty.OverrideMetadata(typeof(TextBlock), new FrameworkPropertyMetadata(IvarFont,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    }

    private ResourceDictionary? _ivarTheme;

    // IVAR design system colours, light or dark as Windows is set. The dictionary goes last, so it wins over the
    // Fluent theme's own values for the same keys.
    private void ApplyIvarTheme()
    {
        var dark = ThemeMode == ThemeMode.Dark || (ThemeMode != ThemeMode.Light && SystemUsesDarkTheme());
        var theme = new ResourceDictionary { Source = new Uri($"Themes/Ivar{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (_ivarTheme is not null) Resources.MergedDictionaries.Remove(_ivarTheme);
        Resources.MergedDictionaries.Add(theme);
        _ivarTheme = theme;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // File operations are journaled step by step, so an unexpected error never leaves a job in an unknown state.
        MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nYou can resume any job in progress from its job log.",
            WpfDialogs.Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
