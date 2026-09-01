using System.Windows;
using Kudaki.App.ViewModels;

namespace Kudaki.App.Views;

// Preferences ダイアログの code-behind。
// VM の RequestClose が呼ばれたら DialogResult をセットして閉じるだけ。
public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose = result =>
        {
            // ダイアログ modal で開く前提 (ShowDialog)。IsLoaded 未完了時は
            // DialogResult 設定でも例外にならないが、安全側で Loaded 待ちにする。
            if (IsLoaded)
            {
                DialogResult = result;
            }
            Close();
        };
    }
}
