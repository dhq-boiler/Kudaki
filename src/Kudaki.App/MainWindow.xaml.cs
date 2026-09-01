using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kudaki.App.Services;
using Kudaki.App.ViewModels;
using R3;

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
        var vm = new MainViewModel(
            new WpfFileDialogService(this),
            new WpfUpdatePromptService(this));
        vm.SetArrowDiagramService(new WpfArrowDiagramService(this));
        DataContext = vm;
        MainViewModel.Current = vm;

        // Landing overlay に表示するバージョン (splash と同じフォーマット v0.1.4)。
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        LandingVersionText.Text = ver is null ? "v0.0.0" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";

        CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));

        // 起動 progress を刻む。UI 生成完了 → Loaded (UI 準備完了) の 2 段階を View 側で報告し、
        // MCP 起動完了と Document ロードは App/ViewModel 側から報告する。
        vm.ReportLoading(30, "起動中");
        Loaded += (_, _) => vm.ReportLoading(50, "UI 準備完了");

        // Landing overlay の可視制御。XAML 側 Visibility バインドで拾えなかったので
        // R3 の Subscribe で code-behind から直接切り替える。
        vm.IsLoading.Subscribe(loading =>
        {
            LandingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        });

        ((App)Application.Current).ScheduleUpdateCheck();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Vm.SelectedTask.Value = e.NewValue as TaskNodeViewModel;
    }

    // 先行タスク ComboBox で候補を選んだ瞬間に AddPredecessor コマンドを叩き、
    // ComboBox 自身は空に戻す (連続追加できるように)。SelectionChanged は
    // VM に流すのが本筋だが、Behaviors 依存を避けるための最小 code-behind。
    private void PredecessorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.SelectedItem is TaskNodeViewModel candidate)
        {
            Vm.AddPredecessorToSelectedCommand.Execute(candidate);
            cb.SelectedItem = null;
        }
    }

    // ツリー行のタイトル TextBox をクリックしても親 TreeViewItem に click が届かず
    // (TextBox が食う) 選択が変わらない WPF の既定挙動を補正する。Preview で先に
    // TreeViewItem.IsSelected=true にしてから、TextBox にフォーカスは普通に渡す
    // (Handled=false のまま) → 選択 + 編集開始が 1 クリックで両立する。
    private void TitleBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var tvi = FindAncestor<TreeViewItem>(tb);
        if (tvi != null && !tvi.IsSelected)
        {
            tvi.IsSelected = true;
        }
    }

    // 同じくタイトル TextBox がフォーカスを掴んでいる間、Up/Down/Home/End が
    // TextBox 内キャレット移動として消費されてツリーナビゲーションが効かない。
    // これらは VM 側の Select*Task コマンドに流して SelectedTask を直接動かす
    // (Excel の「セル編集中でも矢印で移動する」相当)。
    // Left/Right はキャレット移動として残す (単語単位の編集を潰さない)。
    private void TitleBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Vm.SelectNextTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                Vm.SelectPreviousTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Home:
                Vm.SelectFirstTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.End:
                Vm.SelectLastTaskCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
