using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        var app = (App)Application.Current;
        var vm = new MainViewModel(
            new WpfFileDialogService(this),
            new WpfUpdatePromptService(this),
            new WpfPreferencesDialogService(this, app.LanguageService, app.SettingsStore),
            new WpfConfirmDialogService(this),
            app.SettingsStore,
            new WpfApprovalNotificationService(this, app.SettingsStore),
            new WpfAboutDialogService(this));
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
        vm.ReportLoading(30, Kudaki.App.Properties.Strings.Landing_Status_Initializing);
        Loaded += (_, _) => vm.ReportLoading(50, Kudaki.App.Properties.Strings.Landing_Status_Initializing);

        // Landing overlay の可視制御。XAML 側 Visibility バインドで拾えなかったので
        // R3 の Subscribe で code-behind から直接切り替える。
        // Kudaki の起動が速すぎて素で消すとサブリミナル状態になるので、最低 800ms は
        // 見せてから 300ms かけて opacity で fade out する。
        vm.IsLoading.Subscribe(loading =>
        {
            if (loading)
            {
                LandingOverlay.BeginAnimation(OpacityProperty, null);  // 進行中アニメを解除
                LandingOverlay.Opacity = 1.0;
                LandingOverlay.Visibility = Visibility.Visible;
                _landingShownAt = DateTime.Now;
            }
            else
            {
                _ = HideLandingWithMinDisplayTimeAsync();
            }
        });

        // Diff Overlay の Visibility は XAML 側で ActiveDocument.Value.CurrentPendingSet.Value を bind してるので code-behind Subscribe は不要。
        // 旧実装 (Subscribe 経由) は初回 Subscribe 時点の doc instance に固定される問題があった (tab 切替に追従しない)。

        ((App)Application.Current).ScheduleUpdateCheck();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // App.OnExit だとタイミング次第で MainWindow / DataContext が既に無効になっており
    // タブ復元用の PersistOpenDocuments が呼ばれない事象があった (2026-09-02 dogfood で発覚)。
    // MainWindow.Closing 時点なら DataContext が確実に生きているのでここで永続化する。
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            try { vm.PersistOpenDocuments(); }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"[Kudaki.Tabs] persist failed: {ex}"); }
        }
        base.OnClosing(e);
    }

    private DateTime _landingShownAt;
    private static readonly TimeSpan LandingMinDisplay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan LandingFadeOut = TimeSpan.FromMilliseconds(400);

    // ロード完了 (IsLoading=false) を受けて Landing を消す。最低表示時間を守り、
    // 到達までまだ余裕があれば Task.Delay で待ってから opacity で fade out する。
    private async Task HideLandingWithMinDisplayTimeAsync()
    {
        var shown = DateTime.Now - _landingShownAt;
        var remainder = LandingMinDisplay - shown;
        if (remainder > TimeSpan.Zero)
        {
            await Task.Delay(remainder).ConfigureAwait(true);
        }
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(LandingFadeOut),
        };
        anim.Completed += (_, _) => LandingOverlay.Visibility = Visibility.Collapsed;
        LandingOverlay.BeginAnimation(OpacityProperty, anim);
    }

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

    // ツリー行のダブルクリックでタイトル編集に入る (2026-09-03 先生要望)。
    // 単クリックは選択だけ。TreeViewItem 既定のダブルクリック = 開閉トグルは
    // e.Handled で止める (編集したいだけなのに畳まれると邪魔なので)。
    private void TreeItemRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement row || row.DataContext is not TaskNodeViewModel task) return;
        var doc = Vm.ActiveDocument.Value;
        if (doc is null) return;

        doc.SelectedTask.Value = task;
        doc.BeginEditSelectedTitleCommand.Execute(null);
        e.Handled = true;
    }

    // 編集モードに入って TextBox が現れた瞬間にフォーカスを渡して全選択する。
    // Visibility 反映直後はまだフォーカスを受け取れないので Input 優先度で 1 回遅らせる。
    private void TitleBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsVisible) return;
        tb.Dispatcher.BeginInvoke(
            new Action(() => { tb.Focus(); tb.SelectAll(); }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // 別の場所をクリックされたら確定して編集を抜ける。
    private void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Vm.ActiveDocument.Value?.EndEditTitle(revert: false);
    }

    // 編集中のキー処理。
    //   Enter    確定して編集を抜ける (抜けた後の Enter は従来どおり同階層追加)
    //   Escape   編集開始時のタイトルに戻して抜ける
    //   Up/Down  確定して隣の行へ (Excel でセル編集中に上下を押したときと同じ)
    // Left/Right/Home/End はキャレット移動として TextBox に残す。編集モードが
    // 明示的になったので、編集中の矢印は文字単位の移動である方が自然。
    private void TitleBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var doc = Vm.ActiveDocument.Value;
        if (doc is null) return;

        // EndEditTitle 後は TextBox が Collapsed になるので、先に戻り先を掴んでおく。
        var tvi = FindAncestor<TreeViewItem>(tb);

        switch (e.Key)
        {
            case Key.Enter:
                doc.EndEditTitle(revert: false);
                tvi?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                doc.EndEditTitle(revert: true);
                tvi?.Focus();
                e.Handled = true;
                break;
            case Key.Down:
                doc.EndEditTitle(revert: false);
                tvi?.Focus();
                Vm.SelectNextTaskCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                doc.EndEditTitle(revert: false);
                tvi?.Focus();
                Vm.SelectPreviousTaskCommand.Execute(null);
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
