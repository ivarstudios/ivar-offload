using System.Windows;
using System.Windows.Input;

namespace IvarOffload.App;

public partial class UndoWindow : Window
{
    public UndoWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is UndoListViewModel old) old.Chosen -= OnChosen;
            if (e.NewValue is UndoListViewModel model) model.Chosen += OnChosen;
        };
    }

    private void OnChosen(object? sender, EventArgs e) => DialogResult = true;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is UndoListViewModel { UndoCommand: var undo } && undo.CanExecute(null)) undo.Execute(null);
    }
}
