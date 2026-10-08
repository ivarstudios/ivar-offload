using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace IvarOffload.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>A command with a parameter of type <typeparamref name="T"/> (e.g. the item of a list it is bound in).</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T t && (canExecute?.Invoke(t) ?? true);
    public void Execute(object? parameter)
    {
        if (parameter is T t) execute(t);
    }
}

/// <summary>Runs an async action; the command is disabled while it runs. Errors go to <paramref name="onError"/>.</summary>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        catch (Exception e)
        {
            if (onError is not null) onError(e);
            else MessageBox.Show(e.Message, WpfDialogs.Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>true -> Visible; false/null -> Collapsed. Parameter "invert" flips it.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            bool b => b,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Height left for the upper (scrolling) part of the Sort tab: all of it when the file lists are hidden, otherwise
/// enough to keep the lists usable. Values: available height, lists visible, the lists' minimum height, and whether
/// the lists should also get a share of a tall window. On a laptop the lists keep only a few rows, so the preview's
/// summary and its warnings stay in view.
/// </summary>
public sealed class UpperAreaHeight : IMultiValueConverter
{
    public const double ListsMinHeight = 170;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double available = values.Length > 0 && values[0] is double h && h > 0 ? h : double.PositiveInfinity;
        bool lists = values.Length > 1 && values[1] is true;
        if (!lists || double.IsInfinity(available)) return available;
        double min = values.Length > 2 && values[2] is double m ? m : ListsMinHeight;
        bool grow = values.Length <= 3 || values[3] is true;
        return Math.Max(120, available - Math.Max(min, grow ? available * 0.4 : 0));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible while a scroll viewer has more content below what it shows. Values: VerticalOffset, ScrollableHeight.</summary>
public sealed class MoreBelow : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values[0] is double offset && values[1] is double scrollable && scrollable - offset > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A width that shrinks with the space around it: the available width minus a reserve, kept between a minimum and a
/// maximum. Parameter: "reserve,min,max" (for the list filter box next to the tab headers).
/// </summary>
public sealed class ShrinkingWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double[] p = (parameter as string ?? "0,0,0").Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return value is double available && available > 0 ? Math.Clamp(available - p[0], p[1], p[2]) : p[2];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
