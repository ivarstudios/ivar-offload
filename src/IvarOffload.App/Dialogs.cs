using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace IvarOffload.App;

public enum CardAnswer { Cancel, SortAnyway, GoToBackup }

/// <summary>Everything the view models ask the user in a separate window, so it can be replaced in tests.</summary>
public interface IDialogs
{
    /// <summary>OK / Cancel question. True for OK.</summary>
    bool Confirm(string title, string text);
    void Inform(string title, string text, bool isError = false);
    /// <summary>"This looks like a memory card ..." with [Back up this card first] (the safe default, opens the Backup tab) and [Sort anyway].</summary>
    CardAnswer AskMemoryCard(string drive);
    /// <summary>A question with two named answers; true for <paramref name="primary"/> (the default), false for the other or Esc.</summary>
    bool AskChoice(string heading, string text, string primary, string secondary);
    string? PickFolder(string title, string? start);
    string? PickFile(string title, string filter, string? start);
    /// <summary>The "Undo a previous sort" list. Returns the sort to undo (and what was opened to find it), or null.</summary>
    UndoChoice? PickSortToUndo();
}

/// <summary>The real dialogs. Questions are standard message boxes (UI Automation scripts answer them by button id).</summary>
public sealed class WpfDialogs : IDialogs
{
    public const string Caption = "IVAR Offload";

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public bool Confirm(string title, string text) =>
        (Owner is { } owner
            ? MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;

    public void Inform(string title, string text, bool isError = false)
    {
        MessageBoxImage image = isError ? MessageBoxImage.Error : MessageBoxImage.Information;
        if (Owner is { } owner) MessageBox.Show(owner, text, title, MessageBoxButton.OK, image);
        else MessageBox.Show(text, title, MessageBoxButton.OK, image);
    }

    public CardAnswer AskMemoryCard(string drive)
    {
        var dialog = new ChoiceDialog("Memory card or camera drive?", JobTexts.MemoryCardQuestion(drive), JobTexts.MemoryCardDontSort, "Sort anyway") { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Choice switch
        {
            ChoiceDialog.Result.Primary => CardAnswer.GoToBackup,
            ChoiceDialog.Result.Secondary => CardAnswer.SortAnyway,
            _ => CardAnswer.Cancel,
        };
    }

    public bool AskChoice(string heading, string text, string primary, string secondary)
    {
        var dialog = new ChoiceDialog(heading, text, primary, secondary) { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Choice == ChoiceDialog.Result.Primary;
    }

    public string? PickFolder(string title, string? start)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (start is not null && Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter, string? start)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (start is not null && Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public UndoChoice? PickSortToUndo()
    {
        var model = new UndoListViewModel(this);
        var window = new UndoWindow { DataContext = model, Owner = Owner };
        return window.ShowDialog() == true ? model.Choice : null;
    }
}
