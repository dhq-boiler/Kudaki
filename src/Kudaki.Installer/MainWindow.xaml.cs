using System.Windows;
using Kudaki.Installer.ViewModels;

namespace Kudaki.Installer;

// MVVM 純度の指針に則り、コードビハインドは:
//   - VM 生成 & DataContext 注入
//   - VM.RequestClose の Close() 折り返し
//   - アンインストールモードなら Loaded で VM.RunUninstallAsync を叩く
// これだけ。ボタン / 表示切替は全て XAML + VM で完結。
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        var vm = new InstallerViewModel(app.IsUninstallMode);
        vm.RequestClose = () => Close();
        DataContext = vm;

        if (app.IsUninstallMode)
        {
            Loaded += async (_, _) => await vm.RunUninstallAsync();
        }
    }
}
