using System.Windows;
using Kudaki.Installer.ViewModels;

namespace Kudaki.Installer;

// MVVM 純度の指針に則り、コードビハインドは:
//   - VM 生成 & DataContext 注入
//   - VM.RequestClose の Close() 折り返し
//   - モード (--uninstall / --auto-update) 別の Loaded ハンドラ起動
// これだけ。ボタン / 表示切替は全て XAML + VM で完結。
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        var vm = new InstallerViewModel(
            uninstallMode: app.IsUninstallMode,
            autoUpdateMode: app.IsAutoUpdateMode,
            waitForPid: app.WaitForProcessId);
        vm.RequestClose = () => Close();
        DataContext = vm;

        // 自動更新モードでは Window を最小化して開き (ユーザーを驚かせない)、
        // すぐに更新処理を走らせる。エラー時は Error ページに切り替わって
        // ユーザーが確認できるように残る。
        if (app.IsAutoUpdateMode)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Loaded += async (_, _) => await vm.RunAutoUpdateAsync();
        }
        else if (app.IsUninstallMode)
        {
            Loaded += async (_, _) => await vm.RunUninstallAsync();
        }
    }
}
