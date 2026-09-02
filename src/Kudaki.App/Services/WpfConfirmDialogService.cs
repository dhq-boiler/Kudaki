using System.Windows;

namespace Kudaki.App.Services;

// MessageBox に触れる唯一の実装。VM は IConfirmDialogService だけを知る。
public sealed class WpfConfirmDialogService : IConfirmDialogService
{
    private readonly Window _owner;
    public WpfConfirmDialogService(Window owner) { _owner = owner; }

    public ConfirmResult ShowSaveDiscardCancel(string message, string title)
    {
        var result = MessageBox.Show(
            _owner,
            message,
            title,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => ConfirmResult.Save,
            MessageBoxResult.No => ConfirmResult.Discard,
            _ => ConfirmResult.Cancel,
        };
    }
}
