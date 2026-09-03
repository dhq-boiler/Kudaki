using Kudaki.App.Views;
using System.Windows;

namespace Kudaki.App.Services;

// バージョン情報ダイアログを開くだけの薄いサービス。
// IPreferencesDialogService と同じ形 (View 依存を境界にまとめる)。
public interface IAboutDialogService
{
    void Show();
}

public sealed class WpfAboutDialogService : IAboutDialogService
{
    private readonly Window _owner;

    public WpfAboutDialogService(Window owner) => _owner = owner;

    public void Show()
    {
        var dialog = new AboutDialog { Owner = _owner };
        dialog.ShowDialog();
    }
}
