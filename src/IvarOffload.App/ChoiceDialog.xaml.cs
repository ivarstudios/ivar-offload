using System.Windows;
using System.Windows.Input;

namespace IvarOffload.App;

/// <summary>A question with two named answers. The primary one is the safe choice and has the focus; Esc answers neither.</summary>
public partial class ChoiceDialog : Window
{
    public enum Result { None, Primary, Secondary }

    public ChoiceDialog(string heading, string message, string primary, string secondary)
    {
        InitializeComponent();
        Title = WpfDialogs.Caption;
        HeadingText.Text = heading;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    public Result Choice { get; private set; }

    private void OnPrimary(object sender, RoutedEventArgs e) => Answer(Result.Primary);

    private void OnSecondary(object sender, RoutedEventArgs e) => Answer(Result.Secondary);

    private void Answer(Result choice)
    {
        Choice = choice;
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
