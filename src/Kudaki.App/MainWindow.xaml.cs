using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;

namespace Kudaki.App;

// MVVM 純度指針 (feedback-r3-and-mvvm-purity):
//   コードビハインドは View 固有の細かい配線しか書かない。
//   本ファイルには:
//     1. IFileDialogService 実装を注入して VM を組み立てる (View 側の責務)
//     2. TreeView.SelectedItem → VM.SelectedTask の 1 行ブリッジ
//        (WPF の TreeView.SelectedItem が読取専用 DP なので純 XAML では書けない)
//     3. SystemCommands (Minimize / Maximize / Restore / Close) の CommandBinding
//        (WindowChrome の自作タイトルバーから Command 経由で操作するため)
//   以外は書かない。ダイアログ / drag&drop / KeyBinding はすべて XAML と VM に。
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new WpfFileDialogService(this));

        CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));

        ((App)Application.Current).ScheduleUpdateCheck();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Vm.SelectedTask.Value = e.NewValue as TaskNodeViewModel;
    }
}
