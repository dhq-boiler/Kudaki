using System.Windows;
using Kudaki.App.Views;

namespace Kudaki.App.Services;

// カスタム ConfirmDialog (ダークテーマ) を表示する実装。VM は IConfirmDialogService だけを知る。
// OS ネイティブ MessageBox はダークパレットに追従しないため、Views/ConfirmDialog で自前実装した。
public sealed class WpfConfirmDialogService : IConfirmDialogService
{
    private readonly Window _owner;
    public WpfConfirmDialogService(Window owner) { _owner = owner; }

    public ConfirmResult ShowSaveDiscardCancel(string message, string title)
    {
        var dlg = new ConfirmDialog(title, message) { Owner = _owner };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
