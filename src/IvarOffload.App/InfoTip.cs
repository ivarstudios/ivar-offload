using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace IvarOffload.App;

/// <summary>
/// Extra information kept out of sight: a small (i) button, or a quiet link when <see cref="Label"/> is set, that opens
/// its content in a popup. A click anywhere else or Esc closes it. The look is the implicit style in App.xaml.
/// </summary>
public class InfoTip : ContentControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(InfoTip), new PropertyMetadata(""));

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(InfoTip),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PopupWidthProperty =
        DependencyProperty.Register(nameof(PopupWidth), typeof(double), typeof(InfoTip), new PropertyMetadata(380.0));

    public static readonly DependencyProperty PopupAlignmentProperty =
        DependencyProperty.Register(nameof(PopupAlignment), typeof(HorizontalAlignment), typeof(InfoTip), new PropertyMetadata(HorizontalAlignment.Left));

    /// <summary>The link's text; empty for the (i) icon alone.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>The popup's greatest width: its text wraps within it.</summary>
    public double PopupWidth
    {
        get => (double)GetValue(PopupWidthProperty);
        set => SetValue(PopupWidthProperty, value);
    }

    /// <summary>Right: the popup opens to the left, its right edge under the button's (for a button at the window's right edge).</summary>
    public HorizontalAlignment PopupAlignment
    {
        get => (HorizontalAlignment)GetValue(PopupAlignmentProperty);
        set => SetValue(PopupAlignmentProperty, value);
    }

    /// <summary>The side margin around the popup's face that leaves room for its shadow (as in the template).</summary>
    private const double ShadowMargin = 12;

    private ToggleButton? _toggle;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _toggle = GetTemplateChild("PART_Toggle") as ToggleButton;
        if (GetTemplateChild("PART_Popup") is Popup popup)
        {
            popup.Opened += (_, _) =>
            {
                if (_toggle is null) return;
                // While the popup is open, a click on the button only closes it (it must not reopen it at once).
                _toggle.IsHitTestVisible = false;
                if (PopupAlignment == HorizontalAlignment.Right && popup.Child is FrameworkElement face)
                {
                    face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    // The face's right edge under the button's (the shadow margin is part of the popup's width).
                    popup.HorizontalOffset = _toggle.ActualWidth - face.DesiredSize.Width + ShadowMargin;
                }
            };
            popup.Closed += (_, _) => { if (_toggle is not null) _toggle.IsHitTestVisible = true; };
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen)
        {
            IsOpen = false;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
